using System;
using System.Numerics;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>CX-83: the area-transition highlight, owned. Vanilla is
/// <c>Scene::DoHighlightTriggers(bool)</c> @0x462310, called twice by RSP: per highlighted
/// <c>PartTrigger</c> it stencils the trigger's extruded outline as a shadow volume
/// (<c>SetShadowStencilBuffer</c> + front-face incr / back-face decr) and then alpha-blends a camera-sized
/// frustum hull through <c>SetStencilFunc(3, 0x80, ~0)</c>. That needs a STENCIL buffer on the framebuffer it
/// draws into, and a missing one does not lose the effect -- it paints the whole screen, which is what
/// <c>PostChain.DefaultStencilOk</c> existed to prevent.
/// <para>This asks the same question directly, in one fullscreen pass with no stencil anywhere: reconstruct
/// the world point under each pixel from the frame's own depth and test it against the trigger's prism
/// (crossing number in XY, a z range from the ground-snapped outline plus its extrusion half-height). Exact
/// for a concave outline, which the shadow-volume trick is too, and it composites in DISPLAY space over the
/// finished frame -- the space the engine's replay blended in.</para>
/// <para>Two engine facts shrink the job. The bucket filter is <c>+0xd8 != 0 &amp;&amp; half == +0xe0</c>, and
/// <c>CNWCTrigger::UpdateTriggerColor</c> @0x80eff0 ends every branch at
/// <c>AurPartTriggerSetHighlight(t, true, h, true)</c>, so both bytes are always set and only RSP's second
/// call ever matched: what actually varies is the ALPHA, which that function writes as 0.0 for a trigger
/// nobody is pointing at and 0.25/0.35 for one that is. And the pulse branch is dead -- every
/// <c>AurPartTriggerSetHighlightParams</c> call site passes <c>pulse = false, rate = 0</c> -- so
/// <c>PartTrigger+0xf4</c>/<c>+0xf8</c> and the <c>Scene+0x19c</c> phase are not read at all.</para></summary>
internal static unsafe class TriggerHighlight
{
    /// <summary>Highlighted triggers per frame, and outline points across all of them. In play the count is
    /// one -- the trigger under the cursor -- because every other trigger in the bucket carries alpha 0.
    /// ponytail: past either cap the rest of the triggers go unhighlighted for the frame; a census would be
    /// the reason to raise them, not a guess.</summary>
    private const int MaxTriggers = 8, MaxPoints = 512;

    /// <summary>The SSBO layout the fragment stage reads: <c>MaxTriggers</c> headers, then
    /// <c>MaxTriggers</c> colours, then the points. Mirrors <c>CX_TRIG_MAX</c> in
    /// <c>cx_hilitetrig_f.glsl</c>; <see cref="SelfCheck"/> is what keeps the two from drifting.</summary>
    private const int PointBase = 2 * MaxTriggers;
    private const int Vec4s = PointBase + MaxPoints;
    private const uint BindTrig = 30;

    private static readonly float[] _stage = new float[Vec4s * 4];
    private static int _count, _points;
    /// <summary>Enrolments that took at least one trigger (a non-zero highlight alpha), and draws that drew one,
    /// for the DB period line.</summary>
    internal static long Enrolled, Drawn;
    private static uint _prog, _buf;
    private static int _locCount, _locInvVp, _locDepth;
    private static bool _failed, _logged;
    private static ExtenderLog? _log;

    internal static void Attach(ExtenderLog log) => _log = log;

    /// <summary>Once per world frame, in ENGINE context and BEFORE the frame anchor: snapshot the highlighted
    /// triggers. The population is the AREA's triggers (<see cref="Db.DbTriggers.Live"/>) and not the
    /// frame's <c>triggerbucket</c>, with NO in-view test of its own -- deliberately. This pass is a
    /// depth-reconstructed fullscreen decal, so a trigger whose prism covers no visible pixel contributes
    /// nothing and culling it saves one header in an SSBO; and the alpha filter below already narrows the
    /// population to the one trigger under the cursor
    /// (<c>AurPartTriggerSetHighlight</c> writes 0.0 for every other). Allocation-free.</summary>
    internal static void Enroll()
    {
        _count = 0; _points = 0;
        if (_failed) return;
        // No scene BSP and no bucket: the live triggers are DbTriggers' own list.
        ReadOnlySpan<IntPtr> triggers = Db.DbTriggers.Live;
        int n = triggers.Length;
        fixed (float* s = _stage)
            for (int i = 0; i < n && _count < MaxTriggers; i++)
            {
                var t = new PartTrigger(triggers[i]);
                if (t.IsNull || t.Highlighted == 0 || t.Half == 0) continue;
                float alpha = t.HighlightColour[3];
                if (!(alpha > 0f)) continue;
                int np = t.PointCount;
                if (np < 3 || _points + np > MaxPoints) continue;
                Vector3* pts = (Vector3*)t.Points;
                if (pts == null) continue;
                float zmin = float.MaxValue, zmax = float.MinValue;
                for (int p = 0; p < np; p++)
                {
                    Vector3 w = pts[p];
                    s[(PointBase + _points + p) * 4] = w.X;
                    s[(PointBase + _points + p) * 4 + 1] = w.Y;
                    zmin = MathF.Min(zmin, w.Z); zmax = MathF.Max(zmax, w.Z);
                }
                float h = MathF.Abs(t.Height);
                float* hdr = s + _count * 4;
                hdr[0] = _points; hdr[1] = np; hdr[2] = zmin - h; hdr[3] = zmax + h;
                float* col = s + (MaxTriggers + _count) * 4;
                col[0] = t.HighlightColour[0]; col[1] = t.HighlightColour[1];
                col[2] = t.HighlightColour[2]; col[3] = alpha;
                _points += np;
                _count++;
            }
        if (_count > 0) Enrolled++;
    }

    /// <summary>The pass: one fullscreen triangle over the finished display-space frame, alpha-blended
    /// exactly as vanilla's hull is (<c>SetBlendFunc(0, 1)</c> = SRC_ALPHA / ONE_MINUS_SRC_ALPHA). Depth test
    /// off: the depth is an INPUT here, sampled, not tested against.</summary>
    internal static void Draw(Matrix4x4* vp, int* display)
    {
        if (_count == 0 || PostChain.DepthTex == 0 || !EnsureProgram()) return;
        if (!Matrix4x4.Invert(*vp, out Matrix4x4 inv)) return;
        fixed (float* s = _stage)
            Gl.NamedBufferSubData(_buf, IntPtr.Zero, (IntPtr)((PointBase + _points) * 4 * sizeof(float)), s);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindTrig, _buf);
        Gl.ActiveTexture(Gl.TEXTURE0 + TextureSlots.HiliteDepth);
        Gl.BindTexture(Gl.TEXTURE_2D, PostChain.DepthTex);
        Gl.UseProgram(_prog);
        Gl.Uniform1i(_locCount, _count);
        Gl.Uniform1i(_locDepth, (int)TextureSlots.HiliteDepth);
        // raw row-major bytes, read column-major by GLSL: the transpose, so uInvVP * ndc is exact
        Gl.UniformMatrix4fv(_locInvVp, 1, 0, (float*)&inv);
        Gl.Viewport(display[0], display[1], display[2], display[3]);
        new PassState(depth: false, depthWrite: false, blend: true,
                      blendSrc: AlloyRenderer.GlSrcAlpha, blendDst: AlloyRenderer.GlOneMinusSrcAlpha).Apply();
        Gl.BindVertexArray(Vao());
        Gl.DrawArrays(Gl.TRIANGLES, 0, 3);
        Drawn++;
        Gl.BindVertexArray(0);
        Gl.Disable(Gl.BLEND);
        Gl.Enable(Gl.DEPTH_TEST);
        Gl.DepthMask(1);
        if (_logged) return;
        _logged = true;
        _log?.Info($"TriggerHighlight: LIVE -- {_count} highlighted trigger(s), {_points} outline point(s), "
                 + "drawn as an owned depth-reconstructed decal; the engine's DoHighlightTriggers is not "
                 + "called on an Alloy frame and no default-framebuffer stencil is needed");
    }

    private static uint _vao;
    private static uint Vao()
    {
        if (_vao == 0) { uint v; Gl.CreateVertexArrays(1, &v); _vao = v; }
        return _vao;
    }

    private static bool EnsureProgram()
    {
        if (_prog != 0) return true;
        if (_failed || Gl.CreateBuffers == null) return false;
        _prog = GlProgram.Graphics(PostChain.FullscreenVs, ShaderSource.Get("cx_hilitetrig_f.glsl"),
                                   _log, "AlloyRenderer trigger highlight");
        if (_prog == 0)
        {
            _failed = true;
            _log?.Always("TriggerHighlight: program build failed -- area transitions will not highlight on "
                       + "hover this session");
            return false;
        }
        uint b; Gl.CreateBuffers(1, &b); _buf = b;
        Gl.NamedBufferData(_buf, (IntPtr)(Vec4s * 4 * sizeof(float)), null, Gl.DYNAMIC_DRAW);
        _locCount = GlProgram.Loc(_prog, "uTrigCount");
        _locInvVp = GlProgram.Loc(_prog, "uInvVP");
        _locDepth = GlProgram.Loc(_prog, "uTrigDepth");
        return true;
    }

    internal static void ContextLost() { _prog = 0; _buf = 0; _vao = 0; _count = 0; _points = 0; }

    /// <summary>The SSBO layout is written twice -- here and in the GLSL -- and a silent disagreement reads
    /// every trigger's outline at its neighbour's offset. Asserted, not commented.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        string src = ShaderSource.Get("cx_hilitetrig_f.glsl");
        bool ok = src.Contains($"CX_TRIG_MAX = {MaxTriggers};")
               && src.Contains($"binding = {BindTrig})");
        log.Info(ok ? $"TriggerHighlight.SelfCheck: passed (CX_TRIG_MAX {MaxTriggers}, binding {BindTrig})"
                    : "TriggerHighlight.SelfCheck: FAILED -- cx_hilitetrig_f.glsl disagrees with the staged "
                    + "SSBO layout; the trigger highlight would read an outline at the wrong offset");
        return ok;
    }
}
