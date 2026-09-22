using System;
using System.Numerics;
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
/// occlusion ray it integrates into <c>PartLight+0xa0</c>. That fade is kept (RspShim still runs the vcall);
/// the quad becomes a view-aligned billboard of world size, and its occlusion is a depth-disc test against the
/// scene so it slides behind geometry instead of popping.</para></summary>
internal static unsafe class Halos
{
    internal const string ShdHaloVs = "cx_halo_vs", ShdHaloFs = "cx_halo_fs";

    /// <summary>PartLight fields (Ghidra <c>Scene::RenderLensFlares</c>): the MdlNode, the colour, the
    /// integrated occlusion fade (0..1) and the world position. The fade is the flare's own, NOT the shadow
    /// fade -- see <see cref="EngineLayout.LightFlareFade"/>.</summary>
    private static readonly int PlNode = EngineLayout.PartNodeLink, PlColor = EngineLayout.LightColour,
                               PlFade = EngineLayout.LightFlareFade, PlPos = EngineLayout.LightDir;
    /// <summary>Light MdlNode fields (same function): <c>flareradius</c>, the flare count, the
    /// <c>flaresizes</c> float array and the <c>flarecolorshifts</c> vec3 array.</summary>
    private static readonly int NdFlareRadius = EngineLayout.FlareRadius, NdFlareCount = EngineLayout.FlareCount,
                               NdFlareSizes = EngineLayout.FlareSizes, NdFlareShifts = EngineLayout.FlareShifts;
    private static readonly int SceneLightManager = EngineLayout.SceneLightManager;
    private const uint BindHalos = 5, GlTriangleStrip = 0x0005;   // not-an-offset: GL primitive-mode enum
    /// <summary>Two vec4 per halo: (pos.xyz, world radius), (linear rgb with the engine fade folded in, flareradius).</summary>
    private const int Floats = 8;
    private const int InitialHalos = 64;

    private static delegate* unmanaged<IntPtr, IntPtr> _getFlareLights;
    private static ExtenderLog? _log;
    private static float[] _stage = new float[InitialHalos * Floats];
    private static int _count, _ssboCap;
    private static uint _prog, _buf;
    private static int _locView, _locProj, _locDepth, _locGain, _locViz;
    private static bool _failed, _off;
    private static int _drawn;

    /// <summary>The dials are <see cref="Effects"/>' <c>halos</c> / <c>halosize</c> / <c>halogain</c> /
    /// <c>haloviz</c>: one live knob each, so a chat set is what the next launch reads.</summary>
    internal static void Install(ExtenderLog log, HookService hooks)
    {
        _log = log;
        _getFlareLights = (delegate* unmanaged<IntPtr, IntPtr>)Native.Sym(SymbolMap.GetLensFlareLights);
        if (_getFlareLights == null) { _off = true; log.Info("Halos: GetLensFlareLights unresolved -- no halos"); return; }
        log.Info($"Halos: armed -- flare lights drawn as procedural halos (/effects; size {Effects.HaloSize.V:F2} gain {Effects.HaloGain.V:F2})");
    }

    /// <summary>Once per world frame, engine context: snapshot the engine's flare-light list. Allocation-free
    /// past the first growth.</summary>
    internal static void Enroll(IntPtr scene)
    {
        _count = 0;
        if (_off || !Effects.Halos.V || scene == IntPtr.Zero) return;
        IntPtr lm = *(IntPtr*)(scene + SceneLightManager);
        if (lm == IntPtr.Zero) return;
        byte* list = (byte*)_getFlareLights(lm);
        if (list == null) return;
        IntPtr* items = *(IntPtr**)list;
        int n = *(int*)(list + 8);
        if (items == null || n <= 0) return;
        if (n * Floats > _stage.Length) _stage = new float[n * Floats];
        for (int i = 0; i < n; i++)
        {
            byte* pl = (byte*)items[i];
            if (pl == null) continue;
            byte* nd = *(byte**)(pl + PlNode);
            if (nd == null || *(int*)(nd + NdFlareCount) <= 0) continue;
            float fade = *(float*)(pl + PlFade);
            if (fade <= 0f) continue;
            float* sizes = *(float**)(nd + NdFlareSizes);
            float* shift = *(float**)(nd + NdFlareShifts);
            float sz = sizes != null ? sizes[0] : 1f;
            if (sz <= 0f) continue;
            float* c = (float*)(pl + PlColor);
            float r = c[0], g = c[1], b = c[2];
            if (shift != null) { r += shift[0]; g += shift[1]; b += shift[2]; }
            float* d = (float*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref _stage[_count * Floats]);
            d[0] = *(float*)(pl + PlPos); d[1] = *(float*)(pl + PlPos + 4); d[2] = *(float*)(pl + PlPos + 8);
            d[3] = sz * Effects.HaloSize.V;
            d[4] = Lin(r) * fade; d[5] = Lin(g) * fade; d[6] = Lin(b) * fade;
            d[7] = *(float*)(nd + NdFlareRadius);
            _count++;
        }
    }

    private static float Lin(float c) => MathF.Pow(MathF.Max(c, 0f), AlloyLights.SrgbGamma);

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
            Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, _buf);
            Gl.BufferData(Gl.SHADER_STORAGE_BUFFER, (IntPtr)(_ssboCap * Floats * sizeof(float)), null, Gl.DYNAMIC_DRAW);
        }
        fixed (float* s = _stage)
        {
            Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, _buf);
            Gl.BufferSubData(Gl.SHADER_STORAGE_BUFFER, IntPtr.Zero, (IntPtr)(_count * Floats * sizeof(float)), s);
        }
        Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, 0);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindHalos, _buf);
        SceneDepth.Bind();
        Gl.UseProgram(_prog);
        Gl.UniformMatrix4fv(_locView, 1, 0, (float*)view);
        Gl.UniformMatrix4fv(_locProj, 1, 0, (float*)proj);
        Gl.Uniform1i(_locViz, Effects.HaloViz.V);
        Gl.Uniform1i(_locDepth, (int)SceneDepth.Unit);
        Gl.Uniform1f(_locGain, Effects.HaloGain.V);
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

    private static uint _vao;
    /// <summary>An attribute-less VAO: the quad corners come from gl_VertexID.</summary>
    private static uint EmptyVao()
    {
        if (_vao == 0) { uint v; Gl.GenVertexArrays(1, &v); _vao = v; }
        return _vao;
    }

    private static bool EnsureProgram()
    {
        if (_prog != 0) return true;
        if (_failed || Gl.DrawArraysInstancedBaseInstance == null) return false;
        _prog = GlProgram.Graphics(ShdHaloVs, ShaderStages.Source(ShdHaloVs), ShdHaloFs, ShaderStages.Source(ShdHaloFs), _log, "Halos");
        if (_prog == 0) { _failed = true; _log?.Info("Halos: program build failed -- no halos this session"); return false; }
        uint b; Gl.GenBuffers(1, &b); _buf = b; _ssboCap = 0;
        _locView = GlProgram.Loc(_prog, "uView");
        _locProj = GlProgram.Loc(_prog, "uProj");
        _locViz = GlProgram.Loc(_prog, "uViz");
        _locDepth = GlProgram.Loc(_prog, "uSceneDepth");
        _locGain = GlProgram.Loc(_prog, "uGain");
        return true;
    }

    internal static void ContextLost() { _prog = 0; _buf = 0; _vao = 0; _ssboCap = 0; }
    internal static void ResetPeriod() => _drawn = 0;
    internal static string Stats() => _off ? "halos unavailable" : !Effects.Halos.V ? "halos off" : $"halos {_count} flare lights, {_drawn} drawn";
    internal static string Describe() => $"{(_off ? "UNAVAILABLE" : Effects.Halos.V ? "on" : "off")} size {Effects.HaloSize.V:F2} gain {Effects.HaloGain.V:F2} viz {Effects.HaloViz.V}";
}
