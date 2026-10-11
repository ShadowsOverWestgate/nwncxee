using System;
using System.Collections.Generic;
using System.Numerics;
using CX.Alloy.Mdl;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>Procedural light halos: the Alloy replacement for vanilla's lens-flare quads. The population is
/// the engine's own <c>LightManager::GetLensFlareLights</c> list (every light node with <c>lensflares 1</c>),
/// re-read each frame at the enrol slot; the look is a shader pair a hak can override. Vanilla's screen-space
/// near-plane quad becomes a view-aligned billboard of world size, occluded per pixel against the scene.</summary>
internal static unsafe class Halos
{
    internal const string ShdHaloVs = "cx_halo_vs", ShdHaloFs = "cx_halo_fs";
    /// <summary>The occlusion probe, one invocation per halo. Not an overridable stage: it produces a
    /// number the simulation consumes, not a pixel.</summary>
    private const string OccCs = "cx_haloocc_c";

    /// <summary>SSBO binding points, both shared: the draw binds <see cref="BindHalos"/> and the occlusion
    /// probe it runs reads it there before binding <see cref="BindVis"/>.</summary>
    private const uint BindHalos = SsboSlots.Halos, BindVis = SsboSlots.HaloVis;
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

    private static ExtenderLog? _log;
    private static float[] _stage = new float[InitialHalos * Floats];
    /// <summary>The <c>PartLight*</c> behind each staged halo, so a ring sample can be matched back to the
    /// light it measured. Compared, never dereferenced without finding it in THIS frame's list first -- the
    /// area edge frees every one of them.</summary>
    private static IntPtr[] _stageKey = new IntPtr[InitialHalos];
    private static int _count;
    /// <summary>Every GL name and location the context owns, in one object: context loss replaces it. The
    /// fences die with the context like the buffers: forgotten, never deleted.</summary>
    private sealed class HaloGlState
    {
        public uint Prog, Buf, Vao, OccProg;
        public int SsboCap, LocView, LocProj, LocDepth, LocGain, LocViz;
        public int OccLoView, OccLoProj, OccLoDepth, OccLoCount;
        /// <summary>The probe's output (one uint per halo, device-local) and the mapped ring it is copied into.</summary>
        public uint VisDev;
        public GpuReadback VisRead;
        public int VisSeq, VisCap;
    }

    private static HaloGlState _gl = new();
    private static bool _failed, _off;
    private static int _drawn;

    private static readonly int[] _visN = new int[Ring];
    private static IntPtr[] _visKey = new IntPtr[Ring * InitialHalos];
    private static uint[] _vis = new uint[InitialHalos];
    /// <summary>The last sample the probe read back and the keys it measured, for <see cref="Occlude"/>: read
    /// in Alloy's context by <see cref="Collect"/>, consumed on the engine's side with no GL call at all.</summary>
    private static IntPtr[] _readyKey = new IntPtr[InitialHalos];
    private static int _readyN;
    private static long _occLate;
    private static bool _occFailed;
    private static int _occFaded;

    /// <summary>The dials are <see cref="Effects"/>' <c>halos</c> (the brightness, and 0 is the pass off) /
    /// <c>halosize</c> / <c>haloviz</c>: one live knob each, so a chat set is what the next launch reads.</summary>
    internal static void Install(ExtenderLog log, HookService hooks)
    {
        _log = log;
        log.Info($"Halos: armed -- flare lights from the scene database drawn as procedural halos (/effects; size {Effects.HaloSize.V:F2} gain {Effects.Halos.V:F2})");
    }

    private static bool Reaches(Vector3 pos, float flareRadius)
    {
        if (!FrameInputs.HasCamera || !FrameInputs.HasPerspective) return true;
        float slack = flareRadius + Effects.HaloSize.V;
        float far = FrameInputs.CamFar + slack;
        return (pos - FrameInputs.CameraPos).LengthSquared() < far * far;
    }

    /// <summary>Take the probe's last answer (read fenced, two frames late, in Alloy's context) and walk each
    /// flare's <c>PartLight+0xa0</c> toward it at the engine's own rate -- nothing engine-side integrates that
    /// field any more. No GL call: this runs in the ENGINE's context, and a sample whose light is no longer a
    /// halo is dropped. No sample HOLDS the fade; a probe that cannot build says so once.</summary>
    private static void Occlude()
    {
        if (_occFailed || _count == 0) return;
        int have = _readyN;
        if (have <= 0) return;
        float dt = FrameDelta.Delta(FlareDtFloor);
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
            float target = MathF.Min(1f, (s - 1) / (float)VisScale);
            float was = Fade(key), next = Step(was, target, dt);
            if (next != was) _occFaded++;
            SetFade(key, next);
        }
    }

    /// <summary>Each flare's occlusion fade, keyed by the light's rig id: there is no <c>PartLight+0xa0</c> to
    /// integrate it into.</summary>
    private static readonly Dictionary<IntPtr, float> _dbFade = new();

    /// <summary>A flare's integrated occlusion fade (a light not yet measured starts at 0 and fades in, as a fresh
    /// engine light does).</summary>
    private static float Fade(IntPtr key) => _dbFade.TryGetValue(key, out float f) ? f : 0f;

    private static void SetFade(IntPtr key, float v) => _dbFade[key] = v;

    /// <summary>The halo population: every light of <paramref name="scene"/> whose node carries
    /// flares, read off the database the way <c>DbLights</c> reads the rig. The size, shift and radius are the
    /// same three node fields the engine reads off <c>MdlNodeLight</c> -- its "sizes" word (+0xc0) is the list
    /// <c>InternalParseField</c> @0x54baf0 fills from <c>flarepositions</c>, so that is what is read here too.</summary>
    internal static void EnrollDb(Db.DbId scene)
    {
        _count = 0;
        if (_off || !Effects.HalosOn) return;
        int lits = Db.SceneDb.Lights.High, parts = Db.DbInputs.LightCount, n = lits + parts;
        if (n * Floats > _stage.Length) _stage = new float[n * Floats];
        if (n > _stageKey.Length) _stageKey = new IntPtr[n];
        fixed (float* stage = _stage)
        {
            for (int lit = 1; lit < lits; lit++)
                if (Db.DbInputs.LitLight(lit, out Db.DbLight l)) StageDb(stage, in l, scene);
            for (int k = 0; k < parts; k++)
                if (Db.DbInputs.Light(k, out Db.DbLight l) && l.Lit < 0) StageDb(stage, in l, scene);
        }
        Occlude();
        for (int i = 0; i < _count; i++)
        {
            float f = _occFailed ? 1f : Fade(_stageKey[i]);
            f = f > 0f ? MathF.Min(f, 1f) : 0f;
            _stage[i * Floats + 4] *= f; _stage[i * Floats + 5] *= f; _stage[i * Floats + 6] *= f;
        }
    }

    private static void StageDb(float* stage, in Db.DbLight l, Db.DbId scene)
    {
        if (!l.Enabled || !Db.SceneDb.Objects.Valid(l.Obj) || Db.SceneDb.ObjScene[l.Obj.Index].Value != scene.Value) return;
        LightDef? def = Db.DbInputs.LightDefOf(in l);
        if (def == null || def.FlareTextures.Length <= 0) return;
        float size = def.FlarePositions.Length > 0 ? def.FlarePositions[0] : 1f;
        if (size <= 0f || !Reaches(l.Position, def.FlareRadius)) return;
        Vector3 shift = def.FlareColors.Length > 0 ? def.FlareColors[0] : Vector3.Zero;
        float* d = stage + _count * Floats;
        d[0] = l.Position.X; d[1] = l.Position.Y; d[2] = l.Position.Z;
        d[3] = size * Effects.HaloSize.V;
        Vector3 tint = Tint(l.Colour, shift, 1f);
        d[4] = tint.X; d[5] = tint.Y; d[6] = tint.Z;
        d[7] = def.FlareRadius;
        _stageKey[_count++] = l.Id;
    }

    /// <summary>One flare's occlusion fade after one step: it travels toward <paramref name="target"/> at
    /// <see cref="FlareRate"/> per second and never overshoots it. Vanilla's target was binary, so this
    /// reduces to <c>RenderLensFlares</c>' own <c>+/- dt * 3</c> for a target of 0 or 1, and interpolates for
    /// the partial coverage Alloy can measure and a ray cannot.</summary>
    internal static float Step(float fade, float target, float dt)
    {
        float step = FlareRate * dt;
        return fade < target ? MathF.Min(target, fade + step) : MathF.Max(target, fade - step);
    }

    /// <summary>A staged halo's tint: the RAW light colour plus the node's shift, as <c>RenderLensFlares</c>
    /// @0x471860 sums them, scaled down by its brightest channel when that is past 1, linearised, times the
    /// clamped fade. Scaled rather than clamped per channel, which turns an overbright torch white. The cap
    /// keeps the RGBA16F target finite; NaN in any input stages as 0.</summary>
    internal static Vector3 Tint(Vector3 colour, Vector3 shift, float fade)
    {
        Vector3 v = colour + shift;
        v = new Vector3(Finite(v.X), Finite(v.Y), Finite(v.Z));
        float m = MathF.Max(v.X, MathF.Max(v.Y, v.Z));
        if (m > 1f) v /= m;
        float f = fade > 0f ? MathF.Min(fade, 1f) : 0f;
        return new Vector3(MathF.Pow(v.X, AlloyLights.SrgbGamma), MathF.Pow(v.Y, AlloyLights.SrgbGamma), MathF.Pow(v.Z, AlloyLights.SrgbGamma)) * f;
    }

    /// <summary>A channel floored at 0 and capped at a finite bound: NaN reads 0, and an Inf cannot make the divide by
    /// the brightest channel Inf/Inf.</summary>
    private static float Finite(float c) => c > 0f ? MathF.Min(c, 1e6f) : 0f;

    /// <summary>The halo pass: after the particles, where vanilla draws its flares (step 29). Additive
    /// (ONE, ONE), no depth test -- visibility is the shader's own depth-disc read of the scene copy. Hands
    /// back blend off, depth test on, depth mask on.</summary>
    internal static void Draw(Matrix4x4* view, Matrix4x4* proj, int* viewport)
    {
        if (_count == 0 || !EnsureProgram()) return;
        if (!SceneDepth.Take(viewport)) return;
        if (_count > _gl.SsboCap)
        {
            _gl.SsboCap = Math.Max(_count, InitialHalos);
            Gl.NamedBufferData(_gl.Buf, (IntPtr)(_gl.SsboCap * Floats * sizeof(float)), null, Gl.DYNAMIC_DRAW);
        }
        fixed (float* s = _stage)
            Gl.NamedBufferSubData(_gl.Buf, IntPtr.Zero, (IntPtr)(_count * Floats * sizeof(float)), s);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindHalos, _gl.Buf);
        SceneDepth.Bind();
        Probe(view, proj);
        Gl.UseProgram(_gl.Prog);
        Gl.UniformMatrix4fv(_gl.LocView, 1, 0, (float*)view);
        Gl.UniformMatrix4fv(_gl.LocProj, 1, 0, (float*)proj);
        Gl.Uniform1i(_gl.LocViz, Effects.HaloViz.V);
        Gl.Uniform1i(_gl.LocDepth, (int)SceneDepth.Unit);
        Gl.Uniform1f(_gl.LocGain, Effects.Halos.V * AlloyLights.DayDim);
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

    /// <summary>Dispatch this frame's occlusion probe into the ring slot two frames from being read.
    /// Runs with the halo SSBO and <see cref="SceneDepth"/> already bound, before the draw, so it shares both.</summary>
    private static void Probe(Matrix4x4* view, Matrix4x4* proj)
    {
        if (!EnsureProbe()) return;
        Collect();
        int w = _gl.VisSeq % Ring;
        if (_count > _gl.VisCap)
        {
            _gl.VisCap = Math.Max(_count, InitialHalos);
            _visKey = new IntPtr[Ring * _gl.VisCap];
            for (int i = 0; i < Ring; i++) _visN[i] = 0;   // the old keys' indices no longer address this ring
            if (!_gl.VisRead.Ensure((long)_gl.VisCap * sizeof(uint))) { Fail("the driver refused the readback mapping"); return; }
            Gl.EnsureStorage(ref _gl.VisDev, (long)_gl.VisCap * sizeof(uint), true);
        }
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindVis, _gl.VisDev);
        Gl.UseProgram(_gl.OccProg);
        Gl.UniformMatrix4fv(_gl.OccLoView, 1, 0, (float*)view);
        Gl.UniformMatrix4fv(_gl.OccLoProj, 1, 0, (float*)proj);
        Gl.Uniform1i(_gl.OccLoDepth, (int)SceneDepth.Unit);
        Gl.Uniform1i(_gl.OccLoCount, _count);
        Gl.DispatchCompute((uint)((_count + OccGroup - 1) / OccGroup), 1, 1);
        Gl.MemoryBarrier(Gl.SHADER_STORAGE_BARRIER_BIT | Gl.BUFFER_UPDATE_BARRIER_BIT);
        _gl.VisRead.Copy(_gl.VisDev, 0, w, 0, (long)_count * sizeof(uint));
        _gl.VisRead.Fence(w);
        for (int i = 0; i < _count; i++) _visKey[w * _gl.VisCap + i] = _stageKey[i];
        _visN[w] = _count;
        _gl.VisSeq++;
    }

    /// <summary>Take the slot written two probes ago off the mapping, once its copy has landed. It used to be a
    /// <c>glGetNamedBufferSubData</c>, which NVIDIA queues behind the whole frame submitted so far and waits out:
    /// 7-9 ms of CPU a frame the moment an area had one flare light, fence or no fence. A slot still in flight
    /// keeps the previous sample (counted <see cref="_occLate"/>).</summary>
    private static void Collect()
    {
        int r = (_gl.VisSeq + 1) % Ring;
        int have = _visN[r];
        if (have <= 0) return;
        uint* src = (uint*)_gl.VisRead.Ready(r);
        if (src == null) { _occLate++; return; }
        if (have > _vis.Length) _vis = new uint[have];
        if (have > _readyKey.Length) _readyKey = new IntPtr[have];
        for (int i = 0; i < have; i++) { _vis[i] = src[i]; _readyKey[i] = _visKey[r * _gl.VisCap + i]; }
        _readyN = have;
    }

    private static bool EnsureProbe()
    {
        if (_gl.OccProg != 0) return true;
        if (_occFailed) return false;
        if (Gl.DispatchCompute == null || Gl.MemoryBarrier == null || !GpuReadback.EntryPoints)
            return Fail("this context has no compute, no persistent mapping or no fences");
        _gl.OccProg = GlProgram.Compute(OccCs, ShaderSource.Get(OccCs + ".glsl"), _log, "Halos occlusion probe");
        if (_gl.OccProg == 0) return Fail("the probe program did not build");
        _gl.OccLoView = GlProgram.Loc(_gl.OccProg, "uView");
        _gl.OccLoProj = GlProgram.Loc(_gl.OccProg, "uProj");
        _gl.OccLoDepth = GlProgram.Loc(_gl.OccProg, "uSceneDepth");
        _gl.OccLoCount = GlProgram.Loc(_gl.OccProg, "uCount");
        return true;
    }

    private static bool Fail(string why)
    {
        _occFailed = true;
        _log?.Info($"Halos: the flare occlusion fade is OFF for the session -- {why}. Halos keep their "
                 + "per-pixel depth occlusion and draw at full strength; PartLight+0xa0 is left alone.");
        return false;
    }

    /// <summary>An attribute-less VAO: the quad corners come from gl_VertexID.</summary>
    private static uint EmptyVao()
    {
        if (_gl.Vao == 0) { uint v; Gl.CreateVertexArrays(1, &v); _gl.Vao = v; }
        return _gl.Vao;
    }

    private static bool EnsureProgram()
    {
        if (_gl.Prog != 0) return true;
        if (_failed || Gl.DrawArraysInstancedBaseInstance == null) return false;
        _gl.Prog = GlProgram.Graphics(ShdHaloVs, ShaderStages.Source(ShdHaloVs), ShdHaloFs, ShaderStages.Source(ShdHaloFs), _log, "Halos");
        if (_gl.Prog == 0) { _failed = true; _log?.Info("Halos: program build failed -- no halos this session"); return false; }
        uint b; Gl.CreateBuffers(1, &b); _gl.Buf = b; _gl.SsboCap = 0;
        _gl.LocView = GlProgram.Loc(_gl.Prog, "uView");
        _gl.LocProj = GlProgram.Loc(_gl.Prog, "uProj");
        _gl.LocViz = GlProgram.Loc(_gl.Prog, "uViz");
        _gl.LocDepth = GlProgram.Loc(_gl.Prog, "uSceneDepth");
        _gl.LocGain = GlProgram.Loc(_gl.Prog, "uGain");
        return true;
    }

    internal static void ContextLost()
    {
        _gl = new HaloGlState();
        DropSamples();
    }

    /// <summary>Forget the probe samples in flight and the last one read back.</summary>
    private static void DropSamples()
    {
        _readyN = 0;
        for (int i = 0; i < Ring; i++) _visN[i] = 0;
    }

    /// <summary>World edge: the fades and the probe samples in flight are keyed by light-table index, which the
    /// next area re-tenants, so each goes; the new area's flares fade in as fresh engine lights do.</summary>
    internal static void Revoke()
    {
        _dbFade.Clear();
        DropSamples();
    }

    internal static void ResetPeriod() { _drawn = 0; _occFaded = 0; _occLate = 0; }
    internal static string Stats() => _off ? "halos unavailable" : !Effects.HalosOn ? "halos off"
        : $"halos {_count} flare lights, {_drawn} drawn, {(_occFailed ? "occlusion fade OFF" : $"{_occFaded} fading")}"
          + (_occLate > 0 ? $", {_occLate} probe reads deferred (GPU behind)" : "");
    internal static string Describe() => $"{(_off ? "UNAVAILABLE" : Effects.HalosOn ? "on" : "off")} size {Effects.HaloSize.V:F2} gain {Effects.Halos.V:F2} viz {Effects.HaloViz.V}";

    /// <summary>The occlusion integrator, headless. It is the whole of what the port owns on the CPU
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
        check.Expect(Tint(new Vector3(255f, 0f, 0f), Vector3.Zero, 1f) == new Vector3(1f, 0f, 0f), "a light colour past 1 stages at 1, not at 255^2.2");
        check.Expect(Tint(new Vector3(0.5f, 0f, -2f), new Vector3(0.6f, 0f, 0f), 1f) == new Vector3(1f, 0f, 0f), "colour + shift is capped above and floored below");
        check.Expect(Tint(new Vector3(float.NaN, 1f, 1f), Vector3.Zero, 1f).X == 0f && Tint(Vector3.One, Vector3.Zero, float.NaN) == Vector3.Zero,
                     "a NaN colour or fade stages as 0");
        check.Expect(Tint(Vector3.One, Vector3.Zero, 3f) == Vector3.One, "the fade weight is clamped too");
        Vector3 inf = Tint(new Vector3(float.PositiveInfinity, 0.5f, 0f), Vector3.Zero, 1f);
        check.Expect(float.IsFinite(inf.X) && float.IsFinite(inf.Y) && inf.X == 1f, "an infinite colour stages finite, not NaN");
        // the shift joins the RAW colour before the cap, as vanilla sums them: a torch shifted toward blue keeps
        // green and blue level -- normalising first and adding after would put blue above green
        Vector3 shifted = Tint(new Vector3(1.75f, 1.5f, 1f), new Vector3(0f, 0f, 0.5f), 1f);
        check.Expect(shifted.X == 1f && MathF.Abs(shifted.Y - shifted.Z) < 1e-5f && shifted.Y < 0.8f,
                     $"a flare shift is summed into the raw colour before the cap ({shifted.X:F2}, {shifted.Y:F2}, {shifted.Z:F2})");
        // !786 clamped each channel of the raw overbright colour to 1 before the distance weight: every halo white
        Vector3 torch = Tint(new Vector3(1.75f, 1.5f, 1f), Vector3.Zero, 1f);
        check.Expect(torch.X == 1f && torch.Y < 0.8f && torch.Z < 0.4f && torch.Z < torch.Y,
                     $"an overbright torch's halo keeps the torch's hue ({torch.X:F2}, {torch.Y:F2}, {torch.Z:F2}), not white");
        log.Info(check.Ok ? "Halos.SelfCheck: passed" : "Halos.SelfCheck: FAILED -- see above");
    }
}
