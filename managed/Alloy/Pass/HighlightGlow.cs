using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>CX-82: the selection GLOW, a port of <c>Scene::DoHighlightGob</c> @0x470fc0 and
/// <c>WritePartHighlightVertices</c> @0x45fe40 -- the "glow" highlight mode, and whatever the outline pass
/// cannot cover in "outline" mode (an unclaimed source channel, a root with no Alloy draw records).
/// <para>Per highlighted root, and per attached gob that propagates the highlight: the part tree is walked
/// from <c>Gob::GetAdjustedPosition/Orientation/Scale</c>, composing each part's local transform, and every
/// visible non-skin mesh part emits three textured quads through its node's bounding-box centre, one per box
/// plane, scaled about that centre by the pulse. Additive over the finished display-space frame, depth-tested
/// against the scene, unfogged -- the state the engine drew them in.</para></summary>
internal static unsafe class HighlightGlow
{
    /// <summary>Four vec4 per quad: (centre, alpha), (half-axis U, 0), (half-axis V, 0), (rgb, 0).</summary>
    private const int Floats = 16;
    /// <summary>ponytail: a fixed staging ceiling (1 MiB) -- a Tab-highlight-all in a crowded area is the
    /// worst case; past it the rest is NOT DRAWN and logged once. Grow it if the log ever says so.</summary>
    private const int MaxQuads = 16384;
    /// <summary>Guard against a malformed part tree; real trees are a few dozen levels at most.</summary>
    private const int MaxDepth = 128;
    private const uint BindGlow = 29;
    /// <summary>A gob whose <c>+0x108</c> alpha is under this dims its highlight colour by alpha / this
    /// (the 0.4f at 0x12f1ba8).</summary>
    private const float FadeKnee = 0.4f;
    /// <summary>Each quad's fixed coordinate is pulled this far along the eye direction (the -0.001f at
    /// 0x12f1b78), in the part's local frame as the engine does it.</summary>
    private const float EyeBias = -0.001f;

    private static readonly float[] _stage = new float[MaxQuads * Floats];
    private static int _quads, _cap, _dropped;
    private static uint _prog, _buf, _vao;
    private static int _locView, _locProj, _locDepth, _locInvView, _locTex;
    private static bool _failed, _logged, _texLogged, _capLogged, _off;
    private static ExtenderLog? _log;
    private static float* _amp, _freq, _intensity, _size, _deltaT;
    /// <summary>The pulse phase the engine kept at <c>Scene+0xb0</c>; advanced only while something is
    /// highlighted, as there.</summary>
    private static float _phase;
    private static float _pulse, _alphaScale;
    private static Vector3 _eye;
    private static IntPtr _tex;

    internal static void Attach(ExtenderLog log)
    {
        _log = log;
        _amp = (float*)Native.Sym(SymbolMap.GobHighlightAmplitude);
        _freq = (float*)Native.Sym(SymbolMap.GobHighlightFrequency);
        _intensity = (float*)Native.Sym(SymbolMap.GobHighlightIntensity);
        _size = (float*)Native.Sym(SymbolMap.GobHighlightSize);
        _deltaT = (float*)Native.Sym(SymbolMap.DeltaT);
        _off = _amp == null || _freq == null || _intensity == null || _size == null || _deltaT == null;
        if (_off)
            log.Always("HighlightGlow: a Scene::m_fGobHighlight* static or DeltaT unresolved -- the glow "
                     + "highlight is NOT DRAWN (nothing else draws it since CX-82)");
    }

    internal static void Reset() => _quads = 0;

    /// <summary>Once per frame that has a highlight, before any <see cref="Stage"/>: the pulse, the eye, the
    /// scene's glow texture. The engine's own frame-level terms, read live -- <c>CNWCArea::LoadArea</c>
    /// rewrites the four statics on every area load.</summary>
    internal static void Begin(IntPtr scene, Vector3 eye)
    {
        _quads = 0;
        if (_off) return;
        _phase += 2f * *_deltaT * MathF.PI * *_freq;
        while (_phase > 2f * MathF.PI) _phase -= 2f * MathF.PI;
        _pulse = Pulse(_phase, *_amp, *_size);
        _alphaScale = *_intensity;
        _eye = eye;
        _tex = new Scene(scene).HiliteTexture;
    }

    /// <summary>The box scale about its centre: <c>((sin * 0.5 + 0.5) * (0.25 * amplitude) + 1) * size</c>,
    /// disassembled from @0x4710be..0x471101.</summary>
    internal static float Pulse(float phase, float amplitude, float size)
        => ((MathF.Sin(phase) * 0.5f + 0.5f) * (0.25f * amplitude) + 1f) * size;

    /// <summary>Stage one highlighted root: its own tree, then every attached gob that propagates the
    /// highlight, all in the root's colour.</summary>
    internal static void Stage(IntPtr gob, float* colour)
    {
        if (_off || gob == IntPtr.Zero || _tex == IntPtr.Zero) return;
        var g = new Gob(gob);
        var rgb = new Vector3(Math.Clamp(colour[0], 0f, 1f), Math.Clamp(colour[1], 0f, 1f), Math.Clamp(colour[2], 0f, 1f));
        float ga = g.Alpha;
        if (ga < FadeKnee) rgb *= ga / FadeKnee;
        Root(gob, rgb);
        IntPtr* list = stackalloc IntPtr[2];
        list[0] = IntPtr.Zero; list[1] = IntPtr.Zero;
        g.GetAttachedGobs((IntPtr)list);
        int n = (int)list[1];
        IntPtr* kids = (IntPtr*)list[0];
        for (int i = 0; i < n; i++)
        {
            var kid = new Gob(kids[i]);
            Model node = kid.Node;
            if ((!node.IsNull && (node.OwnerFlags & Highlight.SkipFlagBit) != 0) || kid.PropagateHilite == 0) continue;
            Root(kids[i], rgb);
        }
        if (kids != null) NativeMemory.Free(kids);
    }

    private static void Root(IntPtr gob, Vector3 rgb)
    {
        var g = new Gob(gob);
        AurXform.V3 p = AurXform.AdjustedPosition(gob);
        Quaternion rot = AurXform.AdjustedOrientation(gob);
        Walk(g.PartTree, new Vector3(p.X, p.Y, p.Z), rot, g.GetAdjustedScale(), rgb, 0);
    }

    /// <summary><c>WritePartHighlightVertices</c>'s recursion: a zero <c>part+0x34</c> prunes the subtree, the
    /// local transform composes onto the parent's, and a mesh part that is not a skin emits its box.</summary>
    private static void Walk(Part part, Vector3 pos, Quaternion rot, float scale, Vector3 rgb, int depth)
    {
        if (part.IsNull || depth > MaxDepth || part.Visible == 0) return;
        pos += Vector3.Transform(part.Pos * scale, rot);
        rot *= part.Rot;
        scale *= part.Scale;
        if (!part.AsPartTriMesh().IsNull && part.AsPartSkin() == IntPtr.Zero)
        {
            MdlNode node = part.Node;
            if (!node.IsNull && (node.Kind & EngineLayout.MdlNodeKindTriMesh) == EngineLayout.MdlNodeKindTriMesh)
            {
                var mesh = new MdlNodeTriMesh(node.Ptr);
                if (_quads + 3 > MaxQuads) { _dropped++; }
                else fixed (float* s = _stage)
                    _quads += Quads(mesh.BboxMin, mesh.BboxMax, pos, rot, scale, _pulse, _eye, _alphaScale, rgb,
                                    s + _quads * Floats);
            }
        }
        IntPtr* kids = (IntPtr*)part.Children;
        int n = part.ChildCount;
        for (int i = 0; i < n && kids != null; i++) Walk(new Part(kids[i]), pos, rot, scale, rgb, depth + 1);
    }

    /// <summary>One mesh part's three quads, in the engine's order: the x plane spans (y, z), the y plane
    /// (x, z), the z plane (x, y). Alpha is <c>dot(plane normal, eye direction)^2 * intensity</c>, so a quad
    /// seen edge-on vanishes. A zero-area quad is skipped. Returns the quads written.</summary>
    internal static int Quads(Vector3 bmin, Vector3 bmax, Vector3 pos, Quaternion rot, float scale, float pulse,
                              Vector3 eye, float intensity, Vector3 rgb, float* d)
    {
        Vector3 mid = (bmin + bmax) * (0.5f * scale);
        Vector3 half = (bmax - bmin) * (0.5f * pulse * scale);
        Vector3 toEye = eye - pos;
        float len2 = toEye.LengthSquared();
        // normalize() @0x5b37d0: a zero vector comes back as (1, 0, 0)
        toEye = len2 == 0f ? Vector3.UnitX : toEye * (1f / MathF.Sqrt(len2));
        Vector3 biased = mid + toEye * EyeBias;
        int written = 0;
        for (int a = 0; a < 3; a++)
        {
            int u = a == 0 ? 1 : 0, v = a == 2 ? 1 : 2;
            float hu = ((float*)&half)[u], hv = ((float*)&half)[v];
            if (hu == 0f || hv == 0f) continue;
            Vector3 centre = mid, axisU = default, axisV = default, n = default;
            ((float*)&centre)[a] = ((float*)&biased)[a];
            ((float*)&axisU)[u] = hu;
            ((float*)&axisV)[v] = hv;
            ((float*)&n)[a] = 1f;
            float f = Vector3.Dot(Vector3.Transform(n, rot), toEye);
            Vector3 c = pos + Vector3.Transform(centre, rot), wu = Vector3.Transform(axisU, rot),
                    wv = Vector3.Transform(axisV, rot);
            float* r = d + written * Floats;
            r[0] = c.X; r[1] = c.Y; r[2] = c.Z; r[3] = Math.Clamp(f * f * intensity, 0f, 1f);
            r[4] = wu.X; r[5] = wu.Y; r[6] = wu.Z; r[7] = 0f;
            r[8] = wv.X; r[9] = wv.Y; r[10] = wv.Z; r[11] = 0f;
            r[12] = rgb.X; r[13] = rgb.Y; r[14] = rgb.Z; r[15] = 0f;
            written++;
        }
        return written;
    }

    /// <summary>The pass: one instanced quad per staged record, additive, over the FINISHED display-space
    /// frame, depth-tested in the fragment stage against the frame's own depth. Hands the state back the way
    /// the composite expects it.</summary>
    internal static void Draw(Matrix4x4* view, Matrix4x4* proj, int* display)
    {
        if (_dropped > 0 && !_capLogged)
        {
            _capLogged = true;
            _log?.Always($"HighlightGlow: {_dropped} mesh part(s) past the {MaxQuads}-quad staging cap -- their glow "
                       + "is NOT DRAWN (raise MaxQuads)");
        }
        _dropped = 0;
        if (_quads == 0 || PostChain.DepthTex == 0 || !EnsureProgram()) return;
        int tid = new TextureRef(_tex).OwnedTid();
        if (tid == 0)
        {
            if (!_texLogged)
            {
                _texLogged = true;
                _log?.Always("HighlightGlow: the scene's highlight texture has no GL name -- the glow is NOT DRAWN");
            }
            return;
        }
        if (_quads > _cap)
        {
            _cap = Math.Max(_quads, 1024);
            Gl.NamedBufferData(_buf, (IntPtr)(_cap * Floats * sizeof(float)), null, Gl.DYNAMIC_DRAW);
        }
        fixed (float* s = _stage)
            Gl.NamedBufferSubData(_buf, IntPtr.Zero, (IntPtr)(_quads * Floats * sizeof(float)), s);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindGlow, _buf);
        Gl.ActiveTexture(Gl.TEXTURE0 + TextureSlots.HiliteDepth);
        Gl.BindTexture(Gl.TEXTURE_2D, PostChain.DepthTex);
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.BindTexture(Gl.TEXTURE_2D, (uint)tid);
        Gl.UseProgram(_prog);
        Gl.UniformMatrix4fv(_locView, 1, 0, (float*)view);
        Gl.UniformMatrix4fv(_locProj, 1, 0, (float*)proj);
        Gl.Uniform1i(_locDepth, (int)TextureSlots.HiliteDepth);
        Gl.Uniform1i(_locTex, 0);
        Gl.Uniform2f(_locInvView, 1f / MathF.Max(display[2], 1), 1f / MathF.Max(display[3], 1));
        new PassState(depth: false, depthWrite: false, blend: true,
                      blendSrc: AlloyRenderer.GlOne, blendDst: AlloyRenderer.GlOne).Apply();
        Gl.BindVertexArray(_vao);
        Gl.DrawArraysInstancedBaseInstance(GlTriangleStrip, 0, 4, _quads, 0);
        Gl.BindVertexArray(0);
        Gl.Disable(Gl.BLEND);
        Gl.Enable(Gl.DEPTH_TEST);
        Gl.DepthMask(1);
        if (_logged) return;
        _logged = true;
        _log?.Info($"HighlightGlow: LIVE -- {_quads} glow quad(s) this frame, three per visible mesh part of each "
                 + "highlighted root; the engine's DoHighlightGob is not called on an Alloy frame");
    }

    private const uint GlTriangleStrip = 0x0005;   // not-an-offset: GL primitive-mode enum

    private static bool EnsureProgram()
    {
        if (_prog != 0) return true;
        if (_failed || Gl.DrawArraysInstancedBaseInstance == null || Gl.CreateBuffers == null) return false;
        _prog = GlProgram.Graphics(ShaderSource.Get("cx_hiliteglow_v.glsl"),
                                   ShaderSource.Get("cx_hiliteglow_f.glsl"), _log, "AlloyRenderer highlight glow");
        if (_prog == 0)
        {
            _failed = true;
            _log?.Always("HighlightGlow: program build failed -- the glow highlight is NOT DRAWN this session");
            return false;
        }
        uint b; Gl.CreateBuffers(1, &b); _buf = b; _cap = 0;
        uint v; Gl.CreateVertexArrays(1, &v); _vao = v;
        _locView = GlProgram.Loc(_prog, "uView");
        _locProj = GlProgram.Loc(_prog, "uProj");
        _locDepth = GlProgram.Loc(_prog, "uSceneDepth");
        _locInvView = GlProgram.Loc(_prog, "uInvView");
        _locTex = GlProgram.Loc(_prog, "uTex");
        return true;
    }

    /// <summary>GL names die with the context; the stage array does not.</summary>
    internal static void ContextLost() { _prog = 0; _buf = 0; _vao = 0; _cap = 0; _quads = 0; }

    /// <summary>Off-device: the pulse range and the quad geometry, which fail silently on screen (a glow in
    /// the wrong place, or none).</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "HighlightGlow.SelfCheck");
        // LoadArea's values: amplitude 0.5, size 2.5 -> the box swings 2.5x .. 2.8125x
        check.Expect(MathF.Abs(Pulse(-MathF.PI / 2f, 0.5f, 2.5f) - 2.5f) < 1e-5f
                     && MathF.Abs(Pulse(MathF.PI / 2f, 0.5f, 2.5f) - 2.8125f) < 1e-5f, "the pulse swings size .. size * (1 + amplitude / 4)");

        float* d = stackalloc float[3 * Floats];
        // a unit box centred on (0, 0, 1) at the origin, unrotated, eye straight above
        int q = Quads(new Vector3(-1f, -1f, 0f), new Vector3(1f, 1f, 2f), default, Quaternion.Identity, 1f, 2f,
                      new Vector3(0f, 0f, 10f), 0.3f, Vector3.One, d);
        check.Expect(q == 3, "a solid box gives three quads");
        check.Expect(MathF.Abs(d[0] - 0f) < 1e-3f && MathF.Abs(d[2] - 1f) < 1e-5f && d[5] == 2f && d[10] == 2f,
                     "the x plane sits at the box centre and spans y and z, pulse-scaled");
        check.Expect(d[3] == 0f && d[Floats + 3] == 0f && MathF.Abs(d[2 * Floats + 3] - 0.3f) < 1e-5f,
                     "only the plane facing the eye glows: alpha = dot^2 * intensity");
        check.Expect(MathF.Abs(d[2 * Floats + 2] - (1f - 0.001f)) < 1e-6f, "the fixed coordinate takes the engine's 0.001 eye bias");

        // the gob's transform moves and turns the box: yaw 90 degrees, 2 units along x, scale 2
        Quaternion yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f);
        q = Quads(new Vector3(1f, 0f, 0f), new Vector3(3f, 2f, 2f), new Vector3(2f, 0f, 0f), yaw, 2f, 1f,
                  new Vector3(2f, 0f, 50f), 1f, Vector3.One, d);
        // local centre (2, 1, 1) * 2 = (4, 2, 2); yawed -> (-2, 4, 2); plus the position -> (0, 4, 2)
        check.Expect(q == 3 && MathF.Abs(d[Floats + 0]) < 1e-3f && MathF.Abs(d[Floats + 1] - 4f) < 1e-2f
                     && MathF.Abs(d[Floats + 2] - 2f) < 1e-2f, "the box centre is composed through scale, rotation, position");
        // the x plane's U half-axis is local y (1 * scale 2), which the yaw turns onto -x
        check.Expect(MathF.Abs(d[4] + 2f) < 1e-4f && MathF.Abs(d[5]) < 1e-4f, "the half-axes turn with the part");

        check.Expect(Quads(new Vector3(-1f, -1f, 0f), new Vector3(1f, 1f, 0f), default, Quaternion.Identity, 1f, 1f,
                           new Vector3(0f, 0f, 5f), 1f, Vector3.One, d) == 1, "a flat box keeps only its own plane");

        string f = ShaderSource.Get("cx_hiliteglow_f.glsl");
        check.Expect(f.Contains("if (-vViewZ > CxSceneViewZ(gl_FragCoord.xy * uInvView)) discard;"),
                     "the quads are depth-tested against the scene, as the engine drew them");
        log.Info(check.Ok ? "HighlightGlow.SelfCheck: passed" : "HighlightGlow.SelfCheck: FAILED");
    }
}
