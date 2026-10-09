using System.Numerics;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>
/// CX-51: screen-space reflections, deferred. While the opaque set, the sky, the cutouts and grass draw, the frame
/// target carries two more attachments -- the reflection record (<c>cx_ssrrec</c>: normal, roughness, view depth;
/// the weight the lit stage gave its probe term). At <see cref="PassId.Reflections"/> a half-res pass traces one
/// recorded pixel per 2x2 block through THIS frame's depth (a hi-Z pyramid, <c>cx_ssrhiz_c</c>) and a colour copy
/// of the frame so far (<c>cx_ssrtrace_f</c>); a full-res pass joins that to the pixels at its depth and adds
/// (screen − probe) × trust × each pixel's own weight onto the frame (<c>cx_ssrcomp_f</c>). No lag, no history,
/// and no reflection of a reflection: the copy is taken before any is added.
/// <para>Needs the owned HDR target (<see cref="PostChain.Armed"/>): the records attach to it. Shader-only plus
/// this pass on Windows: no symbol, no offset.</para>
/// </summary>
internal static unsafe class Ssr
{
    private static uint _fbo, _col, _hiz, _recN, _recW, _out, _progHiZ, _progTrace, _progComp;
    private static int _w, _h, _levels, _locLevel, _locNf;
    private static int _tProj, _tInvProj, _tView, _tInvView, _tNf, _tSsr, _tViz, _tPlane, _tColShift, _cCap, _cViz;
    /// <summary>The trace's size: half the frame's, rounded up.</summary>
    private static int _ow, _oh;
    /// <summary>The frame level the colour copy's level 0 stands at: 1 when the trace's finest level is 1 or 2, which
    /// never reads the full-res levels; the copy is then one bilinear 2:1 blit, an exact box, and its mips from there.</summary>
    private static int _colShift;
    /// <summary>The planar reflection's target: the opaque set drawn mirrored (<see cref="AlloyRenderer.PlanarPass"/>),
    /// a fraction of the frame, mipped for rough surfaces; its depth. <see cref="_planeZ"/> is this frame's plane, NaN for none.</summary>
    private static uint _plFbo, _plCol, _plDepth;
    private static int _plW, _plH;
    private static float _planeZ = float.NaN;
    private static bool _armed, _checked;
    private static readonly StandDown _down = new("Ssr");
    private static readonly StandDown _plDown = new("Ssr planar");
    private static ExtenderLog? _log;

    /// <summary>Traces this period, for the [alloy] line.</summary>
    private static int _runs;

    /// <summary>Cell visits per ray at each finest level (full, half, quarter): a coarser confirm needs fewer. A ray
    /// grazing the ground it left never skips a coarse cell (the ground's near edge is in it), so it pays ~2 visits a
    /// pixel; 40 ran dry a few hundred pixels short of a building at low camera pitch and left the probe's sky.</summary>
    private static ReadOnlySpan<float> Steps => [160f, 96f, 48f];

    internal static bool On => Lighting.Ssr.V && !_down.Failed;

    /// <summary>CX-188: the record pair is attached to the frame FBO, so <see cref="GBuffer.ApplyDrawBuffers"/>
    /// routes draw buffers 2..3 at it. The two classes share that list and neither writes it alone.</summary>
    internal static bool RecordsArmed => _armed;

    internal static void Attach(ExtenderLog log) => _log = log;

    /// <summary>Context loss: the names died with it.</summary>
    internal static void Forget()
    {
        _fbo = _col = _hiz = _recN = _recW = _out = _progHiZ = _progTrace = _progComp = 0;
        _plFbo = _plCol = _plDepth = 0; _plW = _plH = 0; _planeZ = float.NaN; _plDown.Forget();
        _w = _h = _levels = 0; _armed = false; _checked = false;
    }

    /// <summary>World edge: nothing is carried across frames, so nothing to drop.</summary>
    internal static void Revoke() { }

    internal static string Stats(int frames)
    {
        string s = $"ssr {(On ? "on" : "off")} ({_runs} traces{(_down.Reason.Length > 0 ? ", last refusal: " + _down.Reason : "")})";
        _runs = 0;
        return s;
    }

    /// <summary>At the opaque pass, with the frame target bound: attach and clear the records. A refusal leaves
    /// them off, which <see cref="Run"/> reads as "the probe alone this frame".</summary>
    internal static void Begin(int* viewport)
    {
        _armed = false;
        if (!On || !PostChain.Armed) return;
        if (Gl.BlitFramebuffer == null || Gl.DispatchCompute == null || Gl.BindImageTexture == null || Gl.DrawBuffers == null || Gl.ClearBufferfv == null)
        { _down.Fail(_log, "glBlitFramebuffer, compute, image stores or draw buffers are missing", "screen-space reflections are off for the session"); return; }
        if (_progTrace == 0 && !BuildPrograms()) return;
        if (viewport[0] != 0 || viewport[1] != 0) { _down.Refuse(_log, "the pass viewport is offset", "no reflections this frame"); return; }
        int bound; Gl.GetIntegerv(Gl.FRAMEBUFFER_BINDING, &bound);
        if ((uint)bound != PostChain.Fbo) { _down.Refuse(_log, "the colour pass is not on the owned frame target", "no reflections this frame"); return; }
        if (!EnsureTargets(viewport[2], viewport[3], Math.Min(Lighting.SsrDetail.V, 1))) { Gl.BindFramebuffer(Gl.FRAMEBUFFER, PostChain.Fbo); return; }
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT2, Gl.TEXTURE_2D, _recN, 0);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT3, Gl.TEXTURE_2D, _recW, 0);
        // armed BEFORE the list is composed: CX-188's G-buffer shares it, and the composer reads this flag
        _armed = true;
        GBuffer.ApplyDrawBuffers();
        float* zero = stackalloc float[4];
        // draw-buffer INDEX 2, not the attachment enum; a zero view depth there is "no record", so the weight needs none
        Gl.ClearBufferfv(Gl.COLOR, 2, zero);
        // once per allocation, not per frame: driver-side validation costs inside the Opaque term
        if (!_checked)
        {
            uint status = Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER);
            if (status != Gl.FRAMEBUFFER_COMPLETE)
            {
                End();
                _down.Fail(_log, $"the frame target is incomplete with the records attached (0x{status:x})", "screen-space reflections are off for the session");
                return;
            }
            _checked = true;
        }
    }

    /// <summary>Around the sky: it writes no record and (CX-188) no G-buffer either, so its fragments stop
    /// paying for either attachment; the cleared record and G0 under it already read "none". Unconditional --
    /// the G-buffer may be armed on a frame Ssr is not. The frame target must be bound.</summary>
    internal static void Pause() => GBuffer.ApplyDrawBuffers(suspend: true);

    internal static void Resume() => GBuffer.ApplyDrawBuffers();

    /// <summary>Detach the records and restore the frame target's single draw buffer. Every frame that reached
    /// <see cref="Begin"/> must come here: the draw-buffer state is the FBO's and outlives the frame.</summary>
    private static void End()
    {
        _armed = false;
        GBuffer.ApplyDrawBuffers();
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT2, Gl.TEXTURE_2D, 0, 0);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT3, Gl.TEXTURE_2D, 0, 0);
    }

    /// <summary>At <see cref="PassId.Reflections"/>, with the frame target bound: close the records, trace, and add
    /// the result onto the frame. Leaves the frame target bound, depth test on, blend OFF.</summary>
    internal static void Run(int* viewport, Matrix4x4* view, Matrix4x4* proj, float near, float far, int viz)
    {
        if (!_armed) return;
        _armed = false;
        End();
        // another light viz paints the lit stage's early out, which writes no record
        float planeZ = _planeZ;
        _planeZ = float.NaN;
        if (viz != 0 && viz != 41 && viz != 42 && viz != 45 && viz != 46) return;
        if (!Matrix4x4.Invert(*proj, out Matrix4x4 invProj) || !Matrix4x4.Invert(*view, out Matrix4x4 invView)) return;
        Gl.DrainErrors(4);
        Gl.BindFramebuffer(Gl.READ_FRAMEBUFFER, PostChain.Fbo);
        Gl.BindFramebuffer(Gl.DRAW_FRAMEBUFFER, _fbo);
        Gl.FramebufferTexture2D(Gl.DRAW_FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, _col, 0);
        Gl.BlitFramebuffer(0, 0, _w, _h, 0, 0, _w >> _colShift, _h >> _colShift, Gl.COLOR_BUFFER_BIT, (uint)(_colShift == 0 ? Gl.NEAREST : Gl.LINEAR));
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _fbo);
        uint err = Gl.GetError == null ? 0 : Gl.GetError();
        if (err != 0)
        {
            Gl.BindFramebuffer(Gl.FRAMEBUFFER, PostChain.Fbo);
            _down.Fail(_log, $"the colour copy raised 0x{err:x}", "screen-space reflections are off for the session");
            return;
        }
        Gl.GenerateTextureMipmap(_col);
        BuildPyramid(near, far);

        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, _out, 0);
        Gl.Viewport(0, 0, _ow, _oh);
        Gl.BindVertexArray(PostChain.FullscreenVao);
        new PassState(depth: false, depthWrite: false).Apply();
        BindUnit(0, Gl.TEXTURE_2D, PostChain.DepthTex);
        BindUnit(1, Gl.TEXTURE_2D, _col);
        BindUnit(2, Gl.TEXTURE_2D, _hiz);
        BindUnit(3, Gl.TEXTURE_2D, _recN);
        BindUnit(4, Gl.TEXTURE_2D, _recW);
        bool probe = EnvProbe.Ready && EnvProbe.Tex != 0;
        BindUnit(5, Gl.TEXTURE_CUBE_MAP, probe ? EnvProbe.Tex : 0);
        EnvProbe.BindSh();   // the probe's origin, for the parallax-corrected fetch the lit stage made
        bool planar = !float.IsNaN(planeZ);
        BindUnit(6, Gl.TEXTURE_2D, planar ? _plCol : 0);
        Gl.UseProgram(_progTrace);
        Gl.UniformMatrix4fv(_tProj, 1, 0, (float*)proj);
        Gl.UniformMatrix4fv(_tInvProj, 1, 0, (float*)&invProj);
        Gl.UniformMatrix4fv(_tView, 1, 0, (float*)view);
        Gl.UniformMatrix4fv(_tInvView, 1, 0, (float*)&invView);
        Gl.Uniform2f(_tNf, near, far);
        int fine = Lighting.SsrDetail.V;
        Gl.Uniform4f(_tSsr, Lighting.SsrRough.V, Steps[fine], probe ? EnvProbe.Mips : 0f, fine);
        Gl.Uniform1i(_tViz, viz);
        Gl.Uniform1i(_tColShift, _colShift);
        Gl.Uniform4f(_tPlane, planar ? planeZ : 0f, planar ? 1f : 0f, 0f, 0f);
        Gl.DrawArrays(Gl.TRIANGLES, 0, 3);
        for (uint u = 6; u >= 1; u--) BindUnit(u, u == 5 ? Gl.TEXTURE_CUBE_MAP : Gl.TEXTURE_2D, 0);

        bool shown = viz is 41 or 42 or 45 or 46;
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, PostChain.Fbo);
        Gl.Viewport(viewport[0], viewport[1], viewport[2], viewport[3]);
        new PassState(depth: false, depthWrite: false, blend: !shown, blendSrc: Gl.ONE, blendDst: Gl.ONE).Apply();
        BindUnit(0, Gl.TEXTURE_2D, _out);
        BindUnit(1, Gl.TEXTURE_2D, _recN);
        BindUnit(2, Gl.TEXTURE_2D, _recW);
        BindUnit(3, Gl.TEXTURE_2D, _hiz);
        Gl.UseProgram(_progComp);
        Gl.Uniform1f(_cCap, Lighting.SsrRough.V);
        Gl.Uniform1i(_cViz, shown ? viz : 0);
        Gl.DrawArrays(Gl.TRIANGLES, 0, 3);
        for (uint u = 3; u >= 1; u--) BindUnit(u, Gl.TEXTURE_2D, 0);
        BindUnit(0, Gl.TEXTURE_2D, 0);
        Gl.Disable(Gl.BLEND);
        Gl.Enable(Gl.DEPTH_TEST); Gl.DepthFunc(Gl.LEQUAL); Gl.DepthMask(1);
        Gl.BindVertexArray(0);
        _runs++;
    }

    /// <summary>Open the planar reflection's target for the mirrored draw: 1/<c>light.ssrplanarres</c> of the frame, cleared to alpha 0 (nothing
    /// mirrored), bound with its viewport. False = no planar reflection this frame, and nothing is bound.</summary>
    internal static bool BeginPlanar(int w, int h, out int pw, out int ph)
    {
        int div = Lighting.SsrPlanarRes.V;
        pw = Math.Max(w / div, 1); ph = Math.Max(h / div, 1);
        if (!_armed || !Lighting.SsrPlanar.V || Gl.FrontFace == null || _plDown.Failed) return false;
        if (_plFbo == 0 || _plW != pw || _plH != ph)
        {
            if (_plFbo == 0) { uint f; Gl.GenFramebuffers(1, &f); _plFbo = f; }
            int levels = 1;
            while ((pw >> levels) > 0 || (ph >> levels) > 0) levels++;
            Fresh(ref _plCol, Gl.RGBA16F, levels, pw, ph, Gl.LINEAR_MIPMAP_LINEAR, Gl.LINEAR);
            RenderTarget.AllocDepth(ref _plDepth, Gl.DEPTH_COMPONENT24, pw, ph);
            Gl.BindFramebuffer(Gl.FRAMEBUFFER, _plFbo);
            Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, _plCol, 0);
            Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.DEPTH_ATTACHMENT, Gl.TEXTURE_2D, _plDepth, 0);
            uint status = Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER);
            Gl.BindFramebuffer(Gl.FRAMEBUFFER, PostChain.Fbo);
            if (status != Gl.FRAMEBUFFER_COMPLETE)
            {
                _plW = _plH = 0;
                return _plDown.Fail(_log, $"the planar target is incomplete (0x{status:x})", "mirror reflections are off for the session");
            }
            _plW = pw; _plH = ph;
            _log?.Info($"Ssr: planar reflection {pw}x{ph} RGBA16F, {levels} mips");
        }
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _plFbo);
        Gl.Viewport(0, 0, pw, ph);
        float* clear = stackalloc float[4] { 0f, 0f, 0f, 0f };
        float one = 1f;
        Gl.DepthMask(1);
        Gl.ClearBufferfv(Gl.COLOR, 0, clear);
        Gl.ClearBufferfv(Gl.DEPTH, 0, &one);
        return true;
    }

    /// <summary>Close the mirrored draw: mip it for rough surfaces, latch the plane for <see cref="Run"/>, and hand the
    /// frame target back (its record draw buffers are the FBO's own state, so they survive).</summary>
    internal static void EndPlanar(float z)
    {
        Gl.GenerateTextureMipmap(_plCol);
        _planeZ = z;
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, PostChain.Fbo);
    }

    private static void BindUnit(uint unit, uint target, uint tex)
    {
        Gl.ActiveTexture(Gl.TEXTURE0 + unit);
        Gl.BindTexture(target, tex);
        if (unit == 0) return;
        Gl.ActiveTexture(Gl.TEXTURE0);
    }

    private static bool BuildPrograms()
    {
        _progHiZ = GlProgram.Compute("cx_ssrhiz_c", ShaderSource.Get("cx_ssrhiz_c.glsl"), _log, "Ssr hi-Z pyramid");
        _progTrace = GlProgram.Graphics(PostChain.FullscreenVs, ShaderSource.Get("cx_ssrtrace_f.glsl"), _log, "Ssr trace", content: false);
        _progComp = GlProgram.Graphics(PostChain.FullscreenVs, ShaderSource.Get("cx_ssrcomp_f.glsl"), _log, "Ssr composite", content: false);
        if (_progHiZ == 0 || _progTrace == 0 || _progComp == 0)
        {
            _progTrace = 0;
            return _down.Fail(_log, "a program did not build", "screen-space reflections are off for the session");
        }
        _locLevel = GlProgram.Loc(_progHiZ, "uLevel");
        _locNf = GlProgram.Loc(_progHiZ, "uNf");
        _tProj = GlProgram.Loc(_progTrace, "uProj"); _tInvProj = GlProgram.Loc(_progTrace, "uInvProj");
        _tView = GlProgram.Loc(_progTrace, "uView"); _tInvView = GlProgram.Loc(_progTrace, "uInvView");
        _tNf = GlProgram.Loc(_progTrace, "uNf"); _tSsr = GlProgram.Loc(_progTrace, "uSsr"); _tViz = GlProgram.Loc(_progTrace, "uViz");
        _tPlane = GlProgram.Loc(_progTrace, "uPlane"); _tColShift = GlProgram.Loc(_progTrace, "uColShift");
        Gl.UseProgram(_progTrace);
        string[] units = ["uDepth", "uColor", "uHiZ", "uRecN", "uRecW", "uProbe", "uPlanar"];
        for (int i = 0; i < units.Length; i++) Gl.Uniform1i(GlProgram.Loc(_progTrace, units[i]), i);
        Gl.UseProgram(_progComp);
        string[] comp = ["uSrc", "uRecN", "uRecW", "uHiZ"];
        for (int i = 0; i < comp.Length; i++) Gl.Uniform1i(GlProgram.Loc(_progComp, comp[i]), i);
        _cCap = GlProgram.Loc(_progComp, "uCap"); _cViz = GlProgram.Loc(_progComp, "uViz");
        Gl.UseProgram(0);
        return true;
    }

    /// <summary>Reduce the frame depth into the pyramid, one dispatch per level. Compute, so no raster state is
    /// touched; the program is put back.</summary>
    private static void BuildPyramid(float near, float far)
    {
        int prev; Gl.GetIntegerv(Gl.CURRENT_PROGRAM, &prev);
        Gl.UseProgram(_progHiZ);
        Gl.Uniform2f(_locNf, near, far);
        // cx_ssrhiz_c's uSrc binding: the same unit the trace reads the finished pyramid on
        Gl.ActiveTexture(Gl.TEXTURE0 + 2);
        for (int i = 0; i < _levels; i++)
        {
            int w = Math.Max(_w >> i, 1), h = Math.Max(_h >> i, 1);
            if (i == 0) Gl.BindTexture(Gl.TEXTURE_2D, PostChain.DepthTex);
            else
            {
                // reading level i-1 while level i is the image: legal, the two never alias
                Gl.BindTexture(Gl.TEXTURE_2D, _hiz);
                Gl.MemoryBarrier(Gl.SHADER_IMAGE_ACCESS_BARRIER_BIT | Gl.TEXTURE_FETCH_BARRIER_BIT);
            }
            Gl.Uniform1i(_locLevel, i);
            Gl.BindImageTexture(0, _hiz, i, 0, 0, Gl.WRITE_ONLY, Gl.R32F);
            Gl.DispatchCompute((uint)(w + 7) / 8, (uint)(h + 7) / 8, 1);
        }
        Gl.MemoryBarrier(Gl.TEXTURE_FETCH_BARRIER_BIT);
        Gl.BindImageTexture(0, 0, 0, 0, 0, Gl.WRITE_ONLY, Gl.R32F);
        Gl.BindTexture(Gl.TEXTURE_2D, 0);
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.UseProgram((uint)prev);
    }

    /// <summary>The records and the pyramid at the frame's size (the records attach beside its colour, so they must
    /// match it), the colour copy (full mip chain, for rough reflections) from level <paramref name="shift"/> of it,
    /// and the trace output at half of it.</summary>
    private static bool EnsureTargets(int w, int h, int shift)
    {
        if (_fbo != 0 && _w == w && _h == h && _colShift == shift) return true;
        _checked = false;
        if (_fbo == 0) { uint f; Gl.GenFramebuffers(1, &f); _fbo = f; }
        int levels = Levels(w, h);
        int cw = Math.Max(w >> shift, 1), ch = Math.Max(h >> shift, 1);
        _ow = (w + 1) / 2; _oh = (h + 1) / 2;
        // immutable storage: a new size is a new name
        Fresh(ref _col, Gl.RGBA16F, Levels(cw, ch), cw, ch, Gl.LINEAR_MIPMAP_LINEAR, Gl.LINEAR);
        Fresh(ref _hiz, Gl.R32F, levels, w, h, Gl.NEAREST_MIPMAP_NEAREST, Gl.NEAREST);
        Fresh(ref _recN, Gl.RGBA16F, 1, w, h, Gl.NEAREST, Gl.NEAREST);
        Fresh(ref _recW, Gl.R11F_G11F_B10F, 1, w, h, Gl.NEAREST, Gl.NEAREST);
        Fresh(ref _out, Gl.RGBA16F, 1, _ow, _oh, Gl.NEAREST, Gl.NEAREST);
        _levels = levels;
        _colShift = shift;
        int prev; Gl.GetIntegerv(Gl.FRAMEBUFFER_BINDING, &prev);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _fbo);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, _out, 0);
        uint status = Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, (uint)prev);
        if (status != Gl.FRAMEBUFFER_COMPLETE)
        {
            _w = _h = 0;
            return _down.Fail(_log, $"the trace framebuffer is incomplete (0x{status:x})", "screen-space reflections are off for the session");
        }
        _w = w; _h = h;
        _log?.Info($"Ssr: {w}x{h}, records RGBA16F + R11F_G11F_B10F, colour copy RGBA16F {cw}x{ch}, trace {_ow}x{_oh}, hi-Z {_levels} levels");
        return true;
    }

    private static int Levels(int w, int h)
    {
        int levels = 1;
        while ((w >> levels) > 0 || (h >> levels) > 0) levels++;
        return levels;
    }

    private static void Fresh(ref uint tex, uint fmt, int levels, int w, int h, int min, int mag)
    {
        uint t;
        if (tex != 0) { t = tex; Gl.DeleteTextures(1, &t); }
        Gl.CreateTextures(Gl.TEXTURE_2D, 1, &t); tex = t;
        Gl.TextureStorage2D(tex, levels, fmt, w, h);
        RenderTarget.Clamp(tex, mag);
        Gl.TextureParameteri(tex, Gl.TEXTURE_MIN_FILTER, min);
        Gl.TextureParameteri(tex, Gl.TEXTURE_MAG_FILTER, mag);
    }
}
