using System;
using System.Diagnostics;
using System.Numerics;
using Sowx.Framework;

namespace Sowx.Alloy;

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
    // MdlNodeDanglyMesh, from MdlNodeDanglyMesh::InternalParseField @0x44da00.
    private const int MdlConstraints = 0x2c8, MdlConstraintCount = 0x2d0;
    private const int MdlDisplacement = 0x2d8, MdlTightness = 0x2dc, MdlPeriod = 0x2e0;
    // MdlNodeTriMesh: the pool token holding the REST positions, and the vertex count.
    private const int MdlRestPool = 0x280, MdlVertexCount = 0x284;
    // PartDanglyMesh's own mutable state: last frame's world pose. The quaternion is w FIRST.
    private const int PartPrevPos = 0xb4, PartPrevRot = 0xc0;
    private const int PartVertexPrim = 0x88;
    // VertexPrimitive: the VDO and the item index its dynamic fork lives in.
    private const int VpVdo = 0x58, VpItem = 0x60;
    /// <summary>VertexPrimitive vtable slot 5. Returns the <c>VertexPrimitiveDangly</c> view whose
    /// <c>+0xa0</c> is the velocity array -- NOT the VertexPrimitive itself (verified at 0x49bd85: the
    /// engine calls this and indexes +0xa0 off the RESULT).</summary>
    private const int VpAsDanglySlot = 0x28;
    private const int VpdVelocity = 0xa0;
    /// <summary>Scene vtable slot 81 -- <c>Scene::IsAnimating</c> @0x45e000. Vanilla gates the whole solve
    /// on it, which is what stops cloaks swaying behind a paused menu.</summary>
    private const int SceneIsAnimatingSlot = 0x288;

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
    private static IntPtr* _slot;
    private static IntPtr* _pCurrentScene;
    private static delegate* unmanaged<IntPtr, Vector3*, float*, void> _getWorld;
    private static delegate* unmanaged<uint, byte*> _getPool;
    private static delegate* unmanaged<IntPtr, uint, byte*> _getItemDyn;
    private static delegate* unmanaged<IntPtr, uint, uint, void> _commitItemDyn;
    private static delegate* unmanaged<IntPtr, void> _genVelocity;
    private static delegate* unmanaged<IntPtr, float, void> _childAnimate;
    private static delegate* unmanaged<IntPtr, Vector3*, float, Vector3> _getWind;
    private static bool _armed;

    // The dials. All taste, all live, so all commands and none in sowx.cfg (ADR 0013).
    private static bool _on = true;
    /// <summary>World units/s^2 down. Low by default ON PURPOSE: rest poses are authored already hanging,
    /// so gravity sags every cloak BELOW its authored rest by g*w/k at equilibrium. This is the dial that
    /// makes existing content look wrong fastest.</summary>
    private static float _gravity = 3.0f;
    /// <summary>Wind acceleration per unit of the engine's wind vector. Vanilla's 20x was a gain on a
    /// POSITION, so it carries no information about what a force should be -- this number is a guess that
    /// wants tuning while looking at a windy area.</summary>
    private static float _wind = 14f;
    /// <summary>How much of the wind is gust rather than steady, 0-1.</summary>
    private static float _turb = 0.7f;
    /// <summary>Spatial and temporal frequency of the gust field. Together they set the apparent wavelength
    /// and speed of the ripple crossing a cloak.</summary>
    private static float _turbFreq = 0.9f, _turbSpeed = 2.6f;
    /// <summary>Per-INSTANCE spread, 0-1: how far two copies of the same model drift apart in gust phase and
    /// gust rate. Two banners built from one MdlNode are identical in every authored number, so without this
    /// they animate in lockstep -- the tell that gives away a prop as a clone.</summary>
    private static float _vary = 0.15f;
    /// <summary>An ambient breeze added to the engine's wind, in the engine's own units. Not decoration:
    /// <c>Gob::Animate</c> @0x49dc91 registers every animated gob as a wind POINT SOURCE, and the Scene's
    /// base wind is zero unless something sets it -- so an area with no script-set wind and nobody moving
    /// nearby has no wind at all, and the gust dials would multiply zero. Set 0 to feel only what the engine
    /// reports.</summary>
    private static float _breeze = 0.35f;
    /// <summary>Body proxy: a vertex may never come closer to the part's own local Z axis than its REST
    /// position was, times this. It is the cheap half of "respect geometry" -- a draped mesh's rest pose
    /// already traces the body it hangs on, so the rest radius IS a body silhouette, free and per-vertex.
    /// <para>The assumption it rests on -- that a dangly node's origin sits near the body axis it drapes --
    /// is CONFIRMED live at 0.95, which is why that is the default. It knows nothing about walls, furniture
    /// or the floor; only the cage bounds those, and consulting real world geometry per vertex per frame is
    /// the general physics engine this design exists to avoid.</para></summary>
    private static float _body = 0.95f;
    /// <summary>Multiplier on vanilla's per-axis cage. 1 is vanilla's freedom of movement; past ~4 cloaks
    /// start reaching the wearer's legs, because nothing here knows the legs are there.</summary>
    private static float _cage = 3.5f;
    /// <summary>Target substep rate. The substep COUNT is derived from the frame's dt, not accumulated --
    /// Animate fires once per part per frame, so an accumulator would advance N times a frame.</summary>
    private static float _hz = 120f;
    private static float _maxSteps = 4f;

    private static int _parts, _verts;

    internal static void Install(ExtenderLog log, HookService hooks)
    {
        _log = log;
        _pCurrentScene = (IntPtr*)Native.Sym(SymbolMap.CurrentScene);
        _getWorld = (delegate* unmanaged<IntPtr, Vector3*, float*, void>)Native.Sym(SymbolMap.PartGetWorld);
        _getPool = (delegate* unmanaged<uint, byte*>)Native.Sym(SymbolMap.GlGetPool);
        _getItemDyn = (delegate* unmanaged<IntPtr, uint, byte*>)Native.Sym(SymbolMap.VdoGetItemDynamicData);
        _commitItemDyn = (delegate* unmanaged<IntPtr, uint, uint, void>)Native.Sym(SymbolMap.VdoCommitItemDynamicData);
        _genVelocity = (delegate* unmanaged<IntPtr, void>)Native.Sym(SymbolMap.VertexPrimGenVelocity);
        _childAnimate = (delegate* unmanaged<IntPtr, float, void>)Native.Sym(SymbolMap.PartAnimate);
        _getWind = (delegate* unmanaged<IntPtr, Vector3*, float, Vector3>)Native.Sym(SymbolMap.SceneGetWindVector);

        if (_pCurrentScene == null || _getWorld == null || _getPool == null || _getItemDyn == null
            || _commitItemDyn == null || _genVelocity == null || _childAnimate == null || _getWind == null)
        {
            log.Info("DanglyPhysics: symbols unresolved -- the engine keeps its own dangly solve");
            return;
        }

        // Chain rather than Install: the solve is a plausible future co-consumer, and #88's rule is that a
        // second Install on the same target silently orphans the first.
        _slot = hooks.Chain(SymbolMap.PartDanglyAnimate,
                            (IntPtr)(delegate* unmanaged<IntPtr, float, void>)&OnAnimate, 10);
        if (_slot == null)
        {
            log.Info("DanglyPhysics: could not chain PartDanglyMesh::Animate -- dangly stays vanilla");
            return;
        }

        _armed = true;
        log.Info($"DanglyPhysics: armed -- substepped dangly at {_hz:F0}Hz with gravity {_gravity:F1}"
                 + $" and wind as a force ({_wind:F0}x, {_turb * 100f:F0}% gust); /dangly on 0 for vanilla");
    }

    /// <summary>The period report, and the measurement the research note could not take: how many dangly
    /// parts and vertices this actually solves per frame. Resets its own counters -- the registry calls this
    /// exactly once per period and nothing else reads them.</summary>
    internal static string Stats(int frames)
    {
        if (frames <= 0 || _parts == 0) { _parts = 0; _verts = 0; return ""; }
        string s = $"dangly {(float)_parts / frames:F1} parts/f, {(float)_verts / frames:F0} verts/f";
        _parts = 0; _verts = 0;
        return s;
    }

    internal static string Describe()
        => $"dangly {(_on && _armed ? "on" : _armed ? "off (vanilla solve)" : "unarmed")} "
         + $"gravity {_gravity:F2} wind {_wind:F2} turb {_turb:F2} turbfreq {_turbFreq:F2} "
         + $"turbspeed {_turbSpeed:F2} vary {_vary:F2} breeze {_breeze:F2} body {(_body > 0f ? _body.ToString("F2") : "off")} cage {_cage:F2} hz {_hz:F0} steps {_maxSteps:F0}";

    /// <summary>Live tuning behind <c>/dangly</c>; returns the old value, NaN for an unknown key. Same
    /// no-lock reasoning as the other passes: independent floats, written from the input thread, re-read
    /// per part.</summary>
    internal static float Tune(string key, float v)
    {
        float old;
        switch (key)
        {
            case "on":        old = _on ? 1f : 0f; _on = v > 0f; break;
            case "gravity":   old = _gravity; _gravity = MathF.Max(v, 0f); break;
            case "wind":      old = _wind; _wind = MathF.Max(v, 0f); break;
            case "turb":      old = _turb; _turb = Math.Clamp(v, 0f, 1f); break;
            case "turbfreq":  old = _turbFreq; _turbFreq = MathF.Max(v, 0f); break;
            case "turbspeed": old = _turbSpeed; _turbSpeed = MathF.Max(v, 0f); break;
            case "vary":      old = _vary; _vary = Math.Clamp(v, 0f, 1f); break;
            case "breeze":    old = _breeze; _breeze = MathF.Max(v, 0f); break;
            case "body":      old = _body; _body = Math.Clamp(v, 0f, 1f); break;
            case "cage":      old = _cage; _cage = MathF.Max(v, 0.01f); break;
            case "hz":        old = _hz; _hz = Math.Clamp(v, 30f, 480f); break;
            case "steps":     old = _maxSteps; _maxSteps = Math.Clamp(MathF.Round(v), 1f, 16f); break;
            default: return float.NaN;
        }
        _log?.Info($"DanglyPhysics: /dangly {key} {v:F2} (was {old:F2}) -> {Describe()}");
        return old;
    }

    internal static void ResetTuning()
    {
        _on = true; _gravity = 3.0f; _wind = 14f; _turb = 0.7f;
        _turbFreq = 0.9f; _turbSpeed = 2.6f; _vary = 0.15f; _breeze = 0.35f; _body = 0.95f;
        _cage = 3.5f; _hz = 120f; _maxSteps = 4f;
        _log?.Info($"DanglyPhysics: /dangly reset -> {Describe()}");
    }

    /// <summary>The replacement solve. Every gate below hands the frame back to the engine rather than
    /// reproducing what it does in that case -- vanilla returns WITHOUT animating the part's children on
    /// each of them, which is an oddity worth inheriting rather than re-deriving.</summary>
    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    private static void OnAnimate(IntPtr part, float dt)
    {
        if (!_on) { Down(part, dt); return; }

        IntPtr scene = *_pCurrentScene;
        if (scene == IntPtr.Zero) { Down(part, dt); return; }
        var isAnimating = (delegate* unmanaged<IntPtr, byte>)(*(IntPtr**)scene)[SceneIsAnimatingSlot / 8];
        if (isAnimating(scene) == 0) { Down(part, dt); return; }

        IntPtr node = *(IntPtr*)(part + AlloyRenderer.PartMdlNode);
        IntPtr vp = *(IntPtr*)(part + PartVertexPrim);
        if (node == IntPtr.Zero || vp == IntPtr.Zero) { Down(part, dt); return; }

        int item = *(int*)(vp + VpItem);
        IntPtr vdo = *(IntPtr*)(vp + VpVdo);
        if (item == -1 || vdo == IntPtr.Zero) { Down(part, dt); return; }

        float* cons = *(float**)(node + MdlConstraints);
        int n = Math.Min(*(ushort*)(node + MdlVertexCount), *(int*)(node + MdlConstraintCount));
        if (cons == null || n <= 0) { Down(part, dt); return; }

        var asDangly = (delegate* unmanaged<IntPtr, IntPtr>)(*(IntPtr**)vp)[VpAsDanglySlot / 8];
        IntPtr vpd = asDangly(vp);
        if (vpd == IntPtr.Zero) { Down(part, dt); return; }
        // Born lazily on the first Animate. Swallowing the original means nobody else ever creates it.
        if (*(IntPtr*)(vpd + VpdVelocity) == IntPtr.Zero) _genVelocity(vpd);
        float* vel = *(float**)(vpd + VpdVelocity);
        if (vel == null) { Down(part, dt); return; }

        float* rest = (float*)_getPool(*(uint*)(node + MdlRestPool));
        float* dst = (float*)_getItemDyn(vdo, (uint)item);
        if (rest == null || dst == null) { Down(part, dt); return; }

        Vector3 wpos;
        float* wrot = stackalloc float[4];
        _getWorld(part, &wpos, wrot);

        // Order matters and is the engine's: teleport test FIRST, seed second. A freshly built part has
        // prevPos = 0, so anywhere but the origin it takes the teleport branch on frame one -- and that
        // memcpy is the only thing that ever initialises the dynamic buffer. Seeding first would skip it
        // and open every spawn by integrating from whatever the buffer held.
        float* prevRot = (float*)(part + PartPrevRot);
        Vector3 ppos = *(Vector3*)(part + PartPrevPos);
        if ((wpos - ppos).Length() > TeleportDist)
        {
            Buffer.MemoryCopy(rest, dst, (long)n * 12, (long)n * 12);
            new Span<float>(vel, n * 3).Clear();
            _commitItemDyn(vdo, (uint)item, 0xffffffff);
            StorePose(part, wpos, wrot);
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
        float rateVar = 1f + ((((hash >> 16) & 0xffff) * (1f / 65535f)) - 0.5f) * 2f * _vary;

        // The part's own motion, carried into every vertex once rather than per substep. This is where all
        // the inertia comes from: without it a cloak sits perfectly still on a sprinting character.
        float iw = -wrot[0], ix = wrot[1], iy = wrot[2], iz = wrot[3];      // inverse rotation (w negated)
        Qmul(iw, ix, iy, iz, prevRot[0], prevRot[1], prevRot[2], prevRot[3],
             out float dw, out float dx, out float dy, out float dz);
        Vector3 tLocal = Qrot(-prevRot[0], prevRot[1], prevRot[2], prevRot[3], ppos - wpos);

        // The area's wind, from the engine, at this part's world position -- the same call vanilla makes and
        // with vanilla's clamped dt. Not WindManager+0x20: the Scene's manager is default-constructed with a
        // ZERO base vector and nothing but the setglobalwind console command ever writes it, so every bit of
        // felt wind in normal play is the point-source term this call composes in.
        Vector3 windW = _getWind(scene, &wpos, Math.Clamp(dt, MinChildDt, MaxChildDt));
        if (!float.IsFinite(windW.X) || !float.IsFinite(windW.Y) || !float.IsFinite(windW.Z)) windW = Vector3.Zero;
        // The breeze wanders rather than blowing down a fixed axis -- a banner pinned to one compass
        // direction forever reads as a bug, and the wander is two trig calls per PART.
        windW += new Vector3(MathF.Cos(t * BreezeTurn), MathF.Sin(t * BreezeTurn), 0f) * _breeze;
        Vector3 windL = Qrot(iw, ix, iy, iz, windW) * _wind;
        Vector3 gravL = Qrot(iw, ix, iy, iz, new Vector3(0f, 0f, -_gravity));
        // A gust direction across the wind, so the flutter is a flap and not just a pulse along one axis.
        Vector3 gust = Vector3.Cross(windL, Qrot(iw, ix, iy, iz, Vector3.UnitZ));
        float gl = gust.Length();
        gust = gl > 1e-4f ? gust * (windL.Length() / gl) : Vector3.Zero;

        // Substeps derived from the frame's dt, never accumulated: Animate fires once per PART per frame,
        // so an accumulator would advance N times a frame. Capping dt first is what bounds h to 1/hz -- a
        // loading hitch otherwise arrives as one 0.25s step and detonates the spring.
        float sdt = MathF.Min(dt, _maxSteps / _hz);
        int steps = Math.Clamp((int)MathF.Round(sdt * _hz), 1, (int)_maxSteps);
        float h = sdt / steps;
        float disp = *(float*)(node + MdlDisplacement);
        float stiff = StiffnessGain * *(float*)(node + MdlTightness);
        float damp = DampingGain * *(float*)(node + MdlPeriod);

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
            float phase = (r.X * 0.71f + r.Y * 1.31f + r.Z * 0.93f) * _turbFreq
                        + phaseOff - t * _turbSpeed * rateVar;
            float g = MathF.Sin(phase);
            float g2 = 1f - 2f * g * g;                 // cos(2*phase), free: no second sine
            Vector3 acc = (windL * (1f + _turb * g) + gust * (_turb * g2) + gravL) * w;

            float k = c * stiff;
            for (int s = 0; s < steps; s++)
            {
                v += (acc + (r - p) * k - v * damp) * h;
                p += v * h;
            }

            float speed = v.Length();
            if (speed > MaxSpeed) v *= MaxSpeed / speed;

            float cap = disp * w * _cage;
            Vector3 d = p - r;
            p = r + new Vector3(Math.Clamp(d.X, -cap, cap),
                                Math.Clamp(d.Y, -cap, cap),
                                Math.Clamp(d.Z, -cap, cap));
            // The body proxy, applied AFTER the cage: it only ever pushes a vertex further OUT, so leaving
            // the cage by a hair is the safe direction. Squared throughout -- the sqrt is paid only by the
            // vertices actually inside the body, which in a settled pose is none of them.
            if (_body > 0f)
            {
                float minSq = (r.X * r.X + r.Y * r.Y) * _body * _body;
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

            dst[o] = p.X; dst[o + 1] = p.Y; dst[o + 2] = p.Z;
            vel[o] = v.X; vel[o + 1] = v.Y; vel[o + 2] = v.Z;
        }

        _commitItemDyn(vdo, (uint)item, 0xffffffff);
        StorePose(part, wpos, wrot);
        _parts++; _verts += n;
        Down_TailOnly(part, dt);
    }

    private static void StorePose(IntPtr part, Vector3 pos, float* rot)
    {
        *(Vector3*)(part + PartPrevPos) = pos;
        float* p = (float*)(part + PartPrevRot);
        p[0] = rot[0]; p[1] = rot[1]; p[2] = rot[2]; p[3] = rot[3];
    }

    /// <summary>Hand the whole call back to the engine.</summary>
    private static void Down(IntPtr part, float dt)
        => ((delegate* unmanaged<IntPtr, float, void>)(*_slot))(part, dt);

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
               && (c - Vector3.UnitX).Length() < 1e-5f;
        log.Info(ok ? "DanglyPhysics.SelfCheck: passed"
                    : $"DanglyPhysics.SelfCheck: FAILED -- quaternion convention wrong (x->{a}, back->{b}, id->{c})");
    }
}
