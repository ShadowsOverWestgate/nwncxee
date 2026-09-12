using System;
using System.Numerics;
using Sowx.Framework;
using Sowx.Graphics;

namespace Sowx.Alloy;

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
    private static int _viz;
    private static bool _failed, _off;
    private static int _drawn;

    private static bool _on = true;
    private static float _size = 0.6f, _gain = 1f;
    private static bool _bootOn; private static float _bootSize, _bootGain;

    internal static void Install(ExtenderLog log, HookService hooks)
    {
        _log = log;
        _getFlareLights = (delegate* unmanaged<IntPtr, IntPtr>)Native.Sym(SymbolMap.GetLensFlareLights);
        _on = Settings.Bool("halos", true);
        _size = Settings.Float("halosize", 0.6f);
        _gain = Settings.Float("halogain", 1f);
        _bootOn = _on; _bootSize = _size; _bootGain = _gain;
        if (_getFlareLights == null) { _off = true; log.Info("Halos: GetLensFlareLights unresolved -- no halos"); return; }
        log.Info($"Halos: armed -- flare lights drawn as procedural halos (/halos; size {_size:F2} gain {_gain:F2})");
    }

    /// <summary>Once per world frame, engine context: snapshot the engine's flare-light list. Allocation-free
    /// past the first growth.</summary>
    internal static void Enroll(IntPtr scene)
    {
        _count = 0;
        if (_off || !_on || scene == IntPtr.Zero) return;
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
            d[3] = sz * _size;
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
        Gl.Uniform1i(_locViz, _viz);
        Gl.Uniform1i(_locDepth, (int)SceneDepth.Unit);
        Gl.Uniform1f(_locGain, _gain);
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
    internal static string Stats() => _off ? "halos unavailable" : !_on ? "halos off" : $"halos {_count} flare lights, {_drawn} drawn";
    internal static string Describe() => $"{(_off ? "UNAVAILABLE" : _on ? "on" : "off")} size {_size:F2} gain {_gain:F2} viz {_viz}";

    internal static float Tune(string key, float v)
    {
        float old;
        switch (key)
        {
            case "on": old = _on ? 1f : 0f; _on = v > 0f; break;
            case "size": old = _size; _size = v; break;
            case "gain": old = _gain; _gain = v; break;
            case "viz": old = _viz; _viz = (int)v; break;
            default: return float.NaN;
        }
        return old;
    }

    internal static void ResetTuning() { _on = _bootOn; _size = _bootSize; _gain = _bootGain; }

    /// <summary>One instanced quad per halo, corners from gl_VertexID, view-aligned. Reach follows vanilla:
    /// gone at <c>flareradius</c>. Occlusion is the fragment stage's, per pixel.</summary>
    internal const string HaloVs = """
        #version 460 core
        layout(std430, binding = 5) readonly buffer SowxHaloBuf { vec4 uHalos[]; };   // (pos, radius), (rgb, flareradius)
        uniform mat4 uView;
        uniform mat4 uProj;
        uniform float uGain;
        out vec2 vUV;
        out vec3 vColor;
        out float vViewZ;     // the light's view-space z (negative forward)
        out float vRadius;    // world radius: the soft occlusion range

        void main()
        {
            vec4 a = uHalos[gl_InstanceID * 2], b = uHalos[gl_InstanceID * 2 + 1];
            vec3 vp = (uView * vec4(a.xyz, 1.0)).xyz;
            float d = length(vp);
            float reach = clamp(1.0 - d / max(b.w, 1e-3), 0.0, 1.0);
            vec2 corner = vec2(float((gl_VertexID & 1) * 2 - 1), float((gl_VertexID >> 1) * 2 - 1));
            vUV = corner;
            vViewZ = vp.z;
            vRadius = a.w;
            float w = reach * uGain;
            vColor = b.rgb * w;
            vec3 pos = vp + vec3(corner * a.w, 0.0);
            gl_Position = w > 0.0 ? uProj * vec4(pos, 1.0) : vec4(2.0, 2.0, 2.0, 1.0);
        }
        """;

    /// <summary>The look: a hot gaussian core, a soft skirt, a faint six-point streak and a slight chromatic
    /// spread (red skirt widest), times a per-pixel occlusion against the scene depth copy (soft over one
    /// radius, so a pillar clips the halo where it covers it). Linear, additive, no encode (#132).
    /// <c>/halos viz 1</c> paints that visibility alone.</summary>
    internal const string HaloFs = """
        #version 460 core
        in vec2 vUV;
        in vec3 vColor;
        in float vViewZ;
        in float vRadius;
        uniform mat4 uProj;
        uniform sampler2D uSceneDepth;
        uniform int uViz;
        out vec4 frag;

        // The scene's view-space DISTANCE (positive) under this fragment, from the depth copy -- the particle pass's own form.
        float SowxSceneViewZ(vec2 uv)
        {
            float ndc = texture(uSceneDepth, uv).r * 2.0 - 1.0;
            return uProj[3].z / (ndc + uProj[2].z);
        }

        void main()
        {
            float r = length(vUV);
            if (r >= 1.0) discard;
            // Per-pixel occlusion: a surface nearer than the light hides this pixel, softly over one radius.
            float sceneDepth = SowxSceneViewZ(gl_FragCoord.xy / vec2(textureSize(uSceneDepth, 0)));   // positive distance
            float lightDepth = -vViewZ;
            float vis = clamp((sceneDepth - (lightDepth - vRadius)) / max(vRadius, 1e-3), 0.0, 1.0);
            if (uViz == 1) { frag = vec4(vec3(vis), 1.0); return; }
            vec3 rr = vec3(r * 0.92, r, r * 1.08);            // chromatic spread: red reaches furthest
            vec3 core = exp(-rr * rr * 22.0);
            vec3 skirt = pow(max(1.0 - rr, 0.0), vec3(3.0)) * 0.35;
            float ang = atan(vUV.y, vUV.x);
            float streak = pow(max(abs(cos(ang * 3.0)), 0.0), 40.0) * pow(1.0 - r, 2.0) * 0.25;
            vec3 profile = core + skirt + streak;
            frag = vec4(vColor * profile * vis, 1.0);
        }
        """;
}
