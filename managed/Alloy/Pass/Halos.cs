using System;
using System.Numerics;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>Procedural light halos: the Alloy replacement for vanilla's lens-flare quads. The population is
/// the engine's own <c>LightManager::GetLensFlareLights</c> list (every light node with <c>lensflares 1</c>),
/// re-read each frame at the enrol slot; the look is a shader pair a hak can override (<c>cx_halo_vs</c>,
/// <c>cx_halo_fs</c>).
/// <para>Vanilla (Ghidra <c>Scene::RenderLensFlares</c> @0x471860) draws each flare as a screen-space quad on
/// the near plane -- which is why it reads as glued to the screen -- sized and dimmed by
/// <c>(flareradius - d) / flareradius</c>, tinted <c>light colour + flarecolorshift</c>, and faded by the
/// occlusion ray it integrates into <c>PartLight+0xa0</c>. The quad becomes a view-aligned billboard of world
/// size, and its per-pixel occlusion is a depth-disc test against the scene so it slides behind geometry
/// instead of popping.</para>
/// <para><b>CX-85: the whole-halo fade is ours too.</b> The shim no longer calls
/// <c>Scene::RenderLensFlares</c>, so nothing engine-side integrates <c>PartLight+0xa0</c> any more. This pass
/// does, off Alloy's own depth: <see cref="OccCs"/> samples <see cref="SceneDepth"/> once per halo, the answer
/// comes back through a <see cref="GpuReadback"/> TWO frames later (so no read ever waits on the GPU), and
/// <see cref="Step"/> walks the field toward it at the engine's own 3-per-second rate. The field is still
/// written, not shadowed, so <c>alloy.enabled=0</c> hands the engine a weight it recognises.</para></summary>
internal static unsafe class Halos
{
    internal const string ShdHaloVs = "cx_halo_vs", ShdHaloFs = "cx_halo_fs";
    /// <summary>CX-85's occlusion probe, one invocation per halo. Not an overridable stage: it produces a
    /// number the simulation consumes, not a pixel.</summary>
    private const string OccCs = "cx_haloocc_c";

    /// <summary>SSBO binding points. <see cref="BindVis"/> is 8 because nothing else in the pipeline uses 8 --
    /// <c>BindBufferBase</c> is global state, and 6 (the lightmap chart buffer) only happens not to collide
    /// because the lit pass draws first.</summary>
    private const uint BindHalos = 5, BindVis = 8;
    private const uint GlTriangleStrip = 0x0005;   // not-an-offset: GL primitive-mode enum
    /// <summary>Two vec4 per halo: (pos.xyz, world radius), (linear rgb with the engine fade folded in, flareradius).</summary>
    private const int Floats = 8;
    private const int InitialHalos = 64;
    /// <summary><c>Scene::RenderLensFlares</c> @0x471860's own integration terms: the occlusion fade travels at
    /// 3.0 per second on a delta floored at 0.02s, so a flare takes a third of a second to appear or vanish
    /// however fast the frame runs.</summary>
    private const float FlareRate = 3.0f, FlareDtFloor = 0.02f;
    /// <summary>The probe's fixed-point scale and its "no sample" slot value -- off screen or behind the
    /// camera, where the fade HOLDS rather than decaying toward an answer nobody measured.</summary>
    private const int VisScale = 1024, VisNone = 0;
    private const int Ring = GpuReadback.Slots;
    private const int OccGroup = 64;   // not-an-offset: local_size_x in cx_haloocc_c.glsl

    private static GetLensFlareLightsFn _getFlareLights;
    private static ExtenderLog? _log;
    private static float[] _stage = new float[InitialHalos * Floats];
    /// <summary>The <c>PartLight*</c> behind each staged halo, so a ring sample can be matched back to the
    /// light it measured. Compared, never dereferenced without finding it in THIS frame's list first -- the
    /// area edge frees every one of them.</summary>
    private static IntPtr[] _stageKey = new IntPtr[InitialHalos];
    private static int _count, _ssboCap;
    private static uint _prog, _buf;
    private static int _locView, _locProj, _locDepth, _locGain, _locViz;
    private static bool _failed, _off;
    private static int _drawn;

    private static uint _occProg;
    /// <summary>The probe's output (one uint per halo, device-local) and the mapped ring it is copied into.</summary>
    private static uint _visDev;
    private static GpuReadback _visRead;
    private static readonly int[] _visN = new int[Ring];
    private static IntPtr[] _visKey = new IntPtr[Ring * InitialHalos];
    private static uint[] _vis = new uint[InitialHalos];
    private static int _visSeq, _visCap;
    /// <summary>The last sample the probe read back and the keys it measured, for <see cref="Occlude"/>: read
    /// in Alloy's context by <see cref="Collect"/>, consumed on the engine's side with no GL call at all.</summary>
    private static IntPtr[] _readyKey = new IntPtr[InitialHalos];
    private static int _readyN;
    private static long _occLate;
    private static int _occLoView, _occLoProj, _occLoDepth, _occLoCount;
    private static bool _occFailed;
    private static int _occFaded;

    /// <summary>The dials are <see cref="Effects"/>' <c>halos</c> / <c>halosize</c> / <c>halogain</c> /
    /// <c>haloviz</c>: one live knob each, so a chat set is what the next launch reads.</summary>
    internal static void Install(ExtenderLog log, HookService hooks)
    {
        _log = log;
        _getFlareLights = EngineFn.GetLensFlareLights;
        if (_getFlareLights == null) { _off = true; log.Info("Halos: GetLensFlareLights unresolved -- no halos"); return; }
        log.Info($"Halos: armed -- flare lights drawn as procedural halos (/effects; size {Effects.HaloSize.V:F2} gain {Effects.HaloGain.V:F2})");
    }

    /// <summary>Once per world frame, engine context: integrate each flare's occlusion fade off the sample the
    /// probe took two frames ago, then snapshot the engine's flare-light list. Allocation-free past the first
    /// growth.</summary>
    internal static void Enroll(IntPtr scene)
    {
        _count = 0;
        if (_off || !Effects.Halos.V || scene == IntPtr.Zero) return;
        // CX-108: the POPULATION is Alloy's light set. `GetLensFlareLights` @0x4c1d80 only filtered the
        // engine's near-camera candidate list by the very node test pass 1 below already applies, so what it
        // bought was the candidate list -- and that is a LightManager read. The registry's set is the whole
        // area, so the distance cut it had for free is made explicit in Reaches().
        bool owned = LightRegistry.Live;
        IntPtr* items = null;
        int n;
        if (owned) n = LightRegistry.Count;
        else
        {
            LightManager lm = new Scene(scene).LightManager;
            if (lm.IsNull) return;
            byte* list = (byte*)_getFlareLights(lm.Ptr);
            if (list == null) return;
            items = *(IntPtr**)list;
            n = *(int*)(list + 8);
            if (items == null) return;
        }
        if (n <= 0) return;
        if (n * Floats > _stage.Length) _stage = new float[n * Floats];
        if (n > _stageKey.Length) _stageKey = new IntPtr[n];

        // Pass 1: who is a halo this frame. The keys have to exist before the fade is integrated, because a
        // ring sample may name a light that has since been freed and this list is what proves it has not.
        for (int i = 0; i < n; i++)
        {
            var pl = new PartLight(owned ? LightRegistry.Light(i).Ptr : items[i]);
            if (pl.IsNull) continue;
            var nd = new MdlNodeLight(new Part(pl.Ptr).Node.Ptr);
            if (nd.IsNull || nd.FlareCount <= 0) continue;
            float* sizes = (float*)nd.FlareSizes;
            if ((sizes != null ? sizes[0] : 1f) <= 0f) continue;
            if (owned && !Reaches(pl, nd)) continue;
            _stageKey[_count++] = pl.Ptr;
        }
        Occlude();

        // Pass 2: the GPU's view of each, with the fade folded into the colour exactly as vanilla folds it.
        fixed (float* stage = _stage)
        for (int i = 0; i < _count; i++)
        {
            var pl = new PartLight(_stageKey[i]);
            var nd = new MdlNodeLight(new Part(pl.Ptr).Node.Ptr);
            float fade = _occFailed ? 1f : pl.FlareFade;
            float* sizes = (float*)nd.FlareSizes;
            float* shift = (float*)nd.FlareShifts;
            Vector3 c = pl.Colour;
            float* d = stage + i * Floats;
            Vector3 wp = pl.Pos;
            d[0] = wp.X; d[1] = wp.Y; d[2] = wp.Z;
            d[3] = (sizes != null ? sizes[0] : 1f) * Effects.HaloSize.V;
            d[4] = Channel(c.X, shift != null ? shift[0] : 0f, fade);
            d[5] = Channel(c.Y, shift != null ? shift[1] : 0f, fade);
            d[6] = Channel(c.Z, shift != null ? shift[2] : 0f, fade);
            d[7] = nd.FlareRadius;
        }
    }

    /// <summary>CX-108: can this flare be in view at all? The engine's flare population was its near-camera
    /// candidate list, so a flare past the scene's far plane was never staged; Alloy's registry is area-wide,
    /// and without this a distant lamp in open air would paint a halo through the fog.
    /// <para>The camera is LAST frame's -- this runs before <see cref="FrameRead"/> -- which is a frame of lag
    /// on a fade that already takes a third of a second. No camera yet keeps every flare, which is the
    /// first-frame answer the rest of the pass expects.</para></summary>
    private static bool Reaches(PartLight light, MdlNodeLight node)
    {
        if (!FrameInputs.HasCamera || !FrameInputs.HasPerspective) return true;
        float slack = node.FlareRadius + Effects.HaloSize.V;
        float far = FrameInputs.CamFar + slack;
        return (light.Pos - FrameInputs.CameraPos).LengthSquared() < far * far;
    }

    /// <summary>Take the probe's last answer (<see cref="Collect"/> read it, fenced, in Alloy's context) and walk
    /// each flare's <c>PartLight+0xa0</c> toward it. No GL call: this runs in the ENGINE's context. A sample whose light is no longer a halo is dropped on the
    /// floor: matching by pointer against this frame's list is what keeps a freed light out of the write.
    /// <para>No sample (off screen, or the probe never ran) HOLDS the fade. That is deliberate and it is the
    /// reason <see cref="_occFailed"/> exists as well: a fade nobody is integrating must not silently read as
    /// "fully occluded", so a probe that cannot build says so once and every halo goes to full brightness with
    /// only its per-pixel occlusion left.</para></summary>
    private static void Occlude()
    {
        if (_occFailed || _count == 0) return;
        int have = _readyN;
        if (have <= 0) return;
        float dt = LightFade.Delta(FlareDtFloor);
        if (dt <= 0f) return;
        _occFaded = 0;
        for (int i = 0; i < have; i++)
        {
            uint s = _vis[i];
            if (s == VisNone) continue;
            IntPtr key = _readyKey[i];
            int live = -1;
            // ponytail: linear match, O(samples x halos) -- tens of flares per view; a key->index map if that grows
            for (int j = 0; j < _count && live < 0; j++) if (_stageKey[j] == key) live = j;
            if (live < 0) continue;
            var pl = new PartLight(key);
            float target = MathF.Min(1f, (s - 1) / (float)VisScale);
            float next = Step(pl.FlareFade, target, dt);
            if (next != pl.FlareFade) _occFaded++;
            pl.FlareFade = next;
        }
    }

    /// <summary>One flare's occlusion fade after one step: it travels toward <paramref name="target"/> at
    /// <see cref="FlareRate"/> per second and never overshoots it. Vanilla's target was binary -- the
    /// occlusion ray either hit or did not -- so this reduces to <c>RenderLensFlares</c>' own
    /// <c>+/- dt * 3</c> for a target of 0 or 1, and interpolates for the partial coverage Alloy can measure
    /// and a ray cannot.</summary>
    internal static float Step(float fade, float target, float dt)
    {
        float step = FlareRate * dt;
        return fade < target ? MathF.Min(target, fade + step) : MathF.Max(target, fade - step);
    }

    /// <summary>One staged halo channel: the light colour plus the node's shift, clamped to [0,1] as
    /// <c>RenderLensFlares</c> @0x471860 clamps each flare channel, linearised, times the clamped fade. An HDR
    /// light colour left unclamped overflows the RGBA16F target to Inf, and the halo's own zero-visibility
    /// pixels turn that into NaN. NaN in either input stages as 0.</summary>
    internal static float Channel(float colour, float shift, float fade)
    {
        float v = colour + shift;
        v = v > 0f ? MathF.Min(v, 1f) : 0f;
        float f = fade > 0f ? MathF.Min(fade, 1f) : 0f;
        return MathF.Pow(v, AlloyLights.SrgbGamma) * f;
    }

    /// <summary>The halo pass: after the particles, where vanilla draws its flares (step 29). Additive
    /// (ONE, ONE), no depth test -- visibility is the shader's own depth-disc read of the scene copy. Hands
    /// back blend off, depth test on, depth mask on.</summary>
    internal static void Draw(Matrix4x4* view, Matrix4x4* proj, int* viewport)
    {
        if (_count == 0 || !EnsureProgram()) return;
        if (!SceneDepth.Take(viewport)) return;
        if (_count > _ssboCap)
        {
            _ssboCap = Math.Max(_count, InitialHalos);
            Gl.NamedBufferData(_buf, (IntPtr)(_ssboCap * Floats * sizeof(float)), null, Gl.DYNAMIC_DRAW);
        }
        fixed (float* s = _stage)
            Gl.NamedBufferSubData(_buf, IntPtr.Zero, (IntPtr)(_count * Floats * sizeof(float)), s);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindHalos, _buf);
        SceneDepth.Bind();
        Probe(view, proj);
        Gl.UseProgram(_prog);
        Gl.UniformMatrix4fv(_locView, 1, 0, (float*)view);
        Gl.UniformMatrix4fv(_locProj, 1, 0, (float*)proj);
        Gl.Uniform1i(_locViz, Effects.HaloViz.V);
        Gl.Uniform1i(_locDepth, (int)SceneDepth.Unit);
        Gl.Uniform1f(_locGain, Effects.HaloGain.V * AlloyLights.DayDim);
        new PassState(depth: false, depthWrite: false, blend: true,
                      blendSrc: AlloyRenderer.GlOne, blendDst: AlloyRenderer.GlOne).Apply();
        Gl.BindVertexArray(EmptyVao());
        Gl.DrawArraysInstancedBaseInstance(GlTriangleStrip, 0, 4, _count, 0);
        Gl.BindVertexArray(0);
        Gl.Disable(Gl.BLEND);
        Gl.Enable(Gl.DEPTH_TEST);
        Gl.DepthMask(1);
        _drawn += _count;
    }

    /// <summary>CX-85: dispatch this frame's occlusion probe into the ring slot two frames from being read.
    /// Runs with the halo SSBO and <see cref="SceneDepth"/> already bound, before the draw, so it shares both.</summary>
    private static void Probe(Matrix4x4* view, Matrix4x4* proj)
    {
        if (!EnsureProbe()) return;
        Collect();
        int w = _visSeq % Ring;
        if (_count > _visCap)
        {
            _visCap = Math.Max(_count, InitialHalos);
            _visKey = new IntPtr[Ring * _visCap];
            for (int i = 0; i < Ring; i++) _visN[i] = 0;   // the old keys' indices no longer address this ring
            if (!_visRead.Ensure((long)_visCap * sizeof(uint))) { Fail("the driver refused the readback mapping"); return; }
            Gl.EnsureStorage(ref _visDev, (long)_visCap * sizeof(uint), true);
        }
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindVis, _visDev);
        Gl.UseProgram(_occProg);
        Gl.UniformMatrix4fv(_occLoView, 1, 0, (float*)view);
        Gl.UniformMatrix4fv(_occLoProj, 1, 0, (float*)proj);
        Gl.Uniform1i(_occLoDepth, (int)SceneDepth.Unit);
        Gl.Uniform1i(_occLoCount, _count);
        Gl.DispatchCompute((uint)((_count + OccGroup - 1) / OccGroup), 1, 1);
        Gl.MemoryBarrier(Gl.SHADER_STORAGE_BARRIER_BIT | Gl.BUFFER_UPDATE_BARRIER_BIT);
        _visRead.Copy(_visDev, 0, w, 0, (long)_count * sizeof(uint));
        _visRead.Fence(w);
        for (int i = 0; i < _count; i++) _visKey[w * _visCap + i] = _stageKey[i];
        _visN[w] = _count;
        _visSeq++;
    }

    /// <summary>Take the slot written two probes ago off the mapping, once its copy has landed. It used to be a
    /// <c>glGetNamedBufferSubData</c>, which NVIDIA queues behind the whole frame submitted so far and waits out:
    /// 7-9 ms of CPU a frame the moment an area had one flare light, fence or no fence. A slot still in flight
    /// keeps the previous sample (counted <see cref="_occLate"/>).</summary>
    private static void Collect()
    {
        int r = (_visSeq + 1) % Ring;
        int have = _visN[r];
        if (have <= 0) return;
        uint* src = (uint*)_visRead.Ready(r);
        if (src == null) { _occLate++; return; }
        if (have > _vis.Length) _vis = new uint[have];
        if (have > _readyKey.Length) _readyKey = new IntPtr[have];
        for (int i = 0; i < have; i++) { _vis[i] = src[i]; _readyKey[i] = _visKey[r * _visCap + i]; }
        _readyN = have;
    }

    private static bool EnsureProbe()
    {
        if (_occProg != 0) return true;
        if (_occFailed) return false;
        if (Gl.DispatchCompute == null || Gl.MemoryBarrier == null || !GpuReadback.EntryPoints)
            return Fail("this context has no compute, no persistent mapping or no fences");
        _occProg = GlProgram.Compute(OccCs, ShaderSource.Get(OccCs + ".glsl"), _log, "Halos occlusion probe");
        if (_occProg == 0) return Fail("the probe program did not build");
        _occLoView = GlProgram.Loc(_occProg, "uView");
        _occLoProj = GlProgram.Loc(_occProg, "uProj");
        _occLoDepth = GlProgram.Loc(_occProg, "uSceneDepth");
        _occLoCount = GlProgram.Loc(_occProg, "uCount");
        return true;
    }

    private static bool Fail(string why)
    {
        _occFailed = true;
        _log?.Info($"Halos: the flare occlusion fade is OFF for the session -- {why}. Halos keep their "
                 + "per-pixel depth occlusion and draw at full strength; PartLight+0xa0 is left alone.");
        return false;
    }

    private static uint _vao;
    /// <summary>An attribute-less VAO: the quad corners come from gl_VertexID.</summary>
    private static uint EmptyVao()
    {
        if (_vao == 0) { uint v; Gl.CreateVertexArrays(1, &v); _vao = v; }
        return _vao;
    }

    private static bool EnsureProgram()
    {
        if (_prog != 0) return true;
        if (_failed || Gl.DrawArraysInstancedBaseInstance == null) return false;
        _prog = GlProgram.Graphics(ShdHaloVs, ShaderStages.Source(ShdHaloVs), ShdHaloFs, ShaderStages.Source(ShdHaloFs), _log, "Halos");
        if (_prog == 0) { _failed = true; _log?.Info("Halos: program build failed -- no halos this session"); return false; }
        uint b; Gl.CreateBuffers(1, &b); _buf = b; _ssboCap = 0;
        _locView = GlProgram.Loc(_prog, "uView");
        _locProj = GlProgram.Loc(_prog, "uProj");
        _locViz = GlProgram.Loc(_prog, "uViz");
        _locDepth = GlProgram.Loc(_prog, "uSceneDepth");
        _locGain = GlProgram.Loc(_prog, "uGain");
        return true;
    }

    internal static void ContextLost()
    {
        _prog = 0; _buf = 0; _vao = 0; _ssboCap = 0;
        _occProg = 0; _visCap = 0; _visSeq = 0; _readyN = 0;
        // The fences died with the context, like the buffers: forgotten, never deleted.
        _visDev = 0; _visRead.ContextLost();
        for (int i = 0; i < Ring; i++) _visN[i] = 0;
    }

    internal static void ResetPeriod() { _drawn = 0; _occFaded = 0; _occLate = 0; }
    internal static string Stats() => _off ? "halos unavailable" : !Effects.Halos.V ? "halos off"
        : $"halos {_count} flare lights, {_drawn} drawn, {(_occFailed ? "occlusion fade OFF" : $"{_occFaded} fading")}"
          + (_occLate > 0 ? $", {_occLate} probe reads deferred (GPU behind)" : "");
    internal static string Describe() => $"{(_off ? "UNAVAILABLE" : Effects.Halos.V ? "on" : "off")} size {Effects.HaloSize.V:F2} gain {Effects.HaloGain.V:F2} viz {Effects.HaloViz.V}";

    /// <summary>CX-85: the occlusion integrator, headless. It is the whole of what the port owns on the CPU
    /// side -- the probe itself has no seam -- and it fails invisibly in both directions: too slow and flares
    /// smear through walls, too fast and they strobe.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        // Vanilla's two cases: a clear ray climbs to 1 and a blocked one falls to 0, both at 3 per second.
        check.Expect(Math.Abs(Step(0f, 1f, 0.1f) - 0.3f) < 1e-5f, "a visible flare climbs at 3 per second");
        check.Expect(Math.Abs(Step(1f, 0f, 0.1f) - 0.7f) < 1e-5f, "an occluded one falls at the same rate");
        check.Expect(Step(0.9f, 1f, 1f) == 1f, "the climb stops at the target, not past it");
        check.Expect(Step(0.1f, 0f, 1f) == 0f, "and so does the fall");
        // Partial coverage is the whole point of measuring depth rather than casting one ray.
        check.Expect(Math.Abs(Step(0.5f, 0.5f, 1f) - 0.5f) < 1e-6f, "a flare already at its coverage holds");
        check.Expect(Step(0f, 0.4f, 1f) == 0.4f, "a partly covered flare settles at its coverage, not at 1");
        // RenderLensFlares @0x471860 clamps each flare channel to [0,1]; an HDR light colour must not reach the
        // RGBA16F target as Inf, where the halo's own zero-visibility pixels turn it into NaN.
        check.Expect(Channel(255f, 0f, 1f) == 1f, "a light colour past 1 stages at 1, not at 255^2.2");
        check.Expect(Channel(0.5f, 0.6f, 1f) == 1f && Channel(-2f, 0f, 1f) == 0f, "colour + shift is clamped both ways");
        check.Expect(Channel(float.NaN, 0f, 1f) == 0f && Channel(1f, 0f, float.NaN) == 0f, "a NaN colour or fade stages as 0");
        check.Expect(Channel(1f, 0f, 3f) == 1f, "the fade weight is clamped too");
        log.Info(check.Ok ? "Halos.SelfCheck: passed" : "Halos.SelfCheck: FAILED -- see above");
    }
}
