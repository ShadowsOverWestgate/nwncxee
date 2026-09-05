using System;
using System.Numerics;
using Sowx.Framework;
using Sowx.Graphics;

namespace Sowx.Alloy;

/// <summary>
/// #388: camera motion vectors. Once per frame, after the opaque depth is final, every render pixel is
/// reprojected through the PREVIOUS frame's view-projection and the screen-space difference is written to
/// an RG16F target. This is exact for every pixel whose model matrix did not move -- most of a NWN frame --
/// and wrong by the object's own motion for the rest; per-object and per-bone history is a later step
/// (docs/research/fsr2-opengl.md, section 5).
/// <para><b>Convention</b>, for the consumer this has none of yet: motion from the CURRENT position to the
/// PREVIOUS one (<c>prev - cur</c>, FSR2's), in RENDER pixels, GL y-up, unjittered (there is no jitter yet).
/// Bounded to +-one target extent per axis and never NaN: a previous clip position with no w falls to zero,
/// and the far plane reprojects like any other depth, so the sky carries the camera's own rotation.</para>
/// <para>Off by default (<c>/post motion 1</c>); <c>/post viz 4</c> runs it regardless and paints it through
/// the resolve. Hot: allocates nothing after the first frame at a size.</para>
/// </summary>
internal static unsafe class MotionVectors
{
    /// <summary><c>GL_RG16F</c>, absent from <see cref="Gl"/>.</summary>
    private const uint Rg16F = 0x822F;

    private static ExtenderLog? _log;
    private static uint _fbo, _tex, _prog;
    private static int _w, _h;
    private static int _locDepth, _locInvVp, _locPrevVp, _locSize;
    private static bool _built, _failed;
    private static Matrix4x4 _prevVp;
    private static bool _hasPrev;
    private static int _lastSeq = -1;
    private static int _runs;

    /// <summary>The velocity target, 0 until the pass has run once at this size.</summary>
    internal static uint Tex => _tex;

    internal static void Attach(ExtenderLog log) => _log = log;

    /// <summary>Context loss: the names are gone, and so is the history they were built against.</summary>
    internal static void Forget()
    { _fbo = 0; _tex = 0; _prog = 0; _w = 0; _h = 0; _built = false; _failed = false; Revoke(); }

    /// <summary>World edge (area transition): the previous camera is another area's. The next frame paints
    /// zero rather than a jump the length of the load screen.</summary>
    internal static void Revoke() { _hasPrev = false; _lastSeq = -1; }

    /// <summary>The pass. Restores the frame's framebuffer and the viewport it was given.</summary>
    internal static void Run(uint depthTex, Matrix4x4* vp, int* viewport, uint restoreFbo, int frameSeq)
    {
        if (_failed || depthTex == 0) return;
        if (!_built) EnsureProgram();
        if (_prog == 0 || !EnsureTarget(viewport[2], viewport[3])) return;
        if (!_hasPrev) { _prevVp = *vp; _hasPrev = true; }
        if (!Matrix4x4.Invert(*vp, out Matrix4x4 inv)) return;
        Matrix4x4 prev = _prevVp;

        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _fbo);
        Gl.Viewport(0, 0, _w, _h);
        Gl.BindVertexArray(PostChain.FullscreenVao);
        new PassState(depth: false, depthWrite: false).Apply();
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.BindTexture(Gl.TEXTURE_2D, depthTex);
        Gl.UseProgram(_prog);
        Gl.Uniform1i(_locDepth, 0);
        // raw row-major bytes, read column-major by GLSL: the transpose, so uInvVP * ndc is exact (Frame.cs)
        Gl.UniformMatrix4fv(_locInvVp, 1, 0, (float*)&inv);
        Gl.UniformMatrix4fv(_locPrevVp, 1, 0, (float*)&prev);
        Gl.Uniform2f(_locSize, _w, _h);
        Gl.DrawArrays(Gl.TRIANGLES, 0, 3);
        Gl.BindTexture(Gl.TEXTURE_2D, 0);
        Gl.Enable(Gl.DEPTH_TEST); Gl.DepthMask(1);
        Gl.BindVertexArray(0);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, restoreFbo);
        Gl.Viewport(viewport[0], viewport[1], viewport[2], viewport[3]);
        // Once per FRAME, not per pass: a second armed pass in the same frame (a reflection subframe) must
        // not make its own camera the next frame's history.
        if (frameSeq != _lastSeq) { _prevVp = *vp; _lastSeq = frameSeq; }
        _runs++;
    }

    internal static string Stats(int frames)
    {
        int n = _runs; _runs = 0;
        return _failed ? "motion STOOD DOWN" : n == 0 ? "motion idle" : $"motion {(float)n / MathF.Max(frames, 1):F2} runs/frame at {_w}x{_h}";
    }

    private static bool EnsureTarget(int w, int h)
    {
        if (_tex != 0 && w == _w && h == _h) return true;
        if (w <= 0 || h <= 0) return false;
        if (_tex == 0)
        {
            uint t; Gl.GenTextures(1, &t); _tex = t;
            uint f; Gl.GenFramebuffers(1, &f); _fbo = f;
        }
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.BindTexture(Gl.TEXTURE_2D, _tex);
        Gl.TexImage2D(Gl.TEXTURE_2D, 0, (int)Rg16F, w, h, 0, Gl.RG, Gl.HALF_FLOAT, null);
        RenderTarget.Clamp(Gl.NEAREST);
        Gl.BindTexture(Gl.TEXTURE_2D, 0);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _fbo);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, _tex, 0);
        uint status = Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER);
        if (status != Gl.FRAMEBUFFER_COMPLETE)
        {
            _failed = true;
            _log?.Info($"MotionVectors: the RG16F target is incomplete (0x{status:x}) -- the pass stands down on this context");
            return false;
        }
        _w = w; _h = h;
        _log?.Info($"MotionVectors: velocity target {w}x{h} RG16F");
        return true;
    }

    private static void EnsureProgram()
    {
        _built = true;
        _prog = GlProgram.Graphics(PostChain.FullscreenVs, Fs, _log, "MotionVectors");
        if (_prog == 0) { _failed = true; return; }
        _locDepth = GlProgram.Loc(_prog, "uDepth"); _locInvVp = GlProgram.Loc(_prog, "uInvVP");
        _locPrevVp = GlProgram.Loc(_prog, "uPrevVP"); _locSize = GlProgram.Loc(_prog, "uSize");
    }

    /// <summary>Depth to NDC (default depth range, so z = 2d-1), through the inverse of this frame's
    /// view-projection and forward through the previous one. The world point stays homogeneous -- the
    /// previous clip w absorbs whatever w the inverse left -- and one divide gives the previous NDC.</summary>
    internal const string Fs = """
        #version 460 core
        in vec2 vUV;
        out vec2 frag;
        uniform sampler2D uDepth;
        uniform mat4 uInvVP;     // this frame's inverse view-projection
        uniform mat4 uPrevVP;    // last frame's view-projection
        uniform vec2 uSize;      // the render size, pixels

        void main()
        {
            float d = texture(uDepth, vUV).r;
            vec4 ndc = vec4(vUV * 2.0 - 1.0, d * 2.0 - 1.0, 1.0);
            vec4 prev = uPrevVP * (uInvVP * ndc);
            if (abs(prev.w) < 1e-6) { frag = vec2(0.0); return; }
            vec2 v = (prev.xy / prev.w - ndc.xy) * 0.5 * uSize;
            if (any(isnan(v)) || any(isinf(v))) v = vec2(0.0);
            frag = clamp(v, -uSize, uSize);
        }
        """;

    /// <summary>Off-device: the stage text carries its convention and its bounds, and the history edge
    /// resets.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"MotionVectors.SelfCheck FAIL: {w}"); } }
        E(Fs.Contains("prev.xy / prev.w - ndc.xy"), "motion is previous minus current, FSR2's convention");
        E(Fs.Contains("abs(prev.w) < 1e-6") && Fs.Contains("isnan(v)") && Fs.Contains("clamp(v, -uSize, uSize)"),
          "no NaN and no unbounded value leaves the stage");
        E(Fs.Contains("d * 2.0 - 1.0"), "depth decodes for the default depth range");
        _hasPrev = true; _lastSeq = 7;
        Revoke();
        E(!_hasPrev && _lastSeq == -1, "Revoke drops the previous camera");
        log.Info(ok ? "MotionVectors.SelfCheck: passed" : "MotionVectors.SelfCheck: FAILED");
        return ok;
    }
}
