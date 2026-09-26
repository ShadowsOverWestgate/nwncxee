using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>
/// #388: camera motion vectors. Once per frame, after the opaque depth is final, every render pixel is
/// reprojected through the PREVIOUS frame's view-projection and the screen-space difference is written to
/// an RG16F target. This is exact for every pixel whose model matrix did not move -- most of a NWN frame --
/// and wrong by the object's own motion for the rest; #392's mover sweep corrects the rigid matrix movers over
/// it, and CX-26 the posed skinmeshes (last frame's palette) and dangly meshes (pre-solve positions).
/// <para><b>Convention</b>: motion from the CURRENT position to the PREVIOUS one (<c>prev - cur</c>, FSR2's),
/// in RENDER pixels, GL y-up, UNJITTERED -- the depth is inverted through the jittered view-projection it was
/// written with, and the current NDC has this frame's jitter (#389) taken back out before the subtraction,
/// so a still camera under TAA paints exactly zero.
/// Bounded to +-one target extent per axis and never NaN: a previous clip position with no w falls to zero,
/// and the far plane reprojects like any other depth, so the sky carries the camera's own rotation.</para>
/// <para>Off by default (<c>/post motion 1</c>); <c>/post viz 4</c> runs it regardless and paints it through
/// the resolve. Hot: allocates nothing after the first frame at a size.</para>
/// </summary>
internal static unsafe class MotionVectors
{

    private static ExtenderLog? _log;
    private static uint _fbo, _tex, _prog;
    private static int _w, _h;
    private static int _locDepth, _locInvVp, _locPrevVp, _locSize, _locJitter;
    private static bool _built, _failed;
    private static Matrix4x4 _prevVp;
    private static bool _hasPrev;
    private static int _lastSeq = -1;
    private static int _runs;

    /// <summary>The velocity target, 0 until the pass has run once at this size.</summary>
    internal static uint Tex => _tex;
    /// <summary>#392: last frame's UNJITTERED view-projection, for the mover sweep -- valid after Run.</summary>
    internal static Matrix4x4 PrevVp => _prevVp;
    /// <summary>Did the pass paint this frame? False until Run has produced a target the consumers may read.</summary>
    internal static bool Painted { get; private set; }
    /// <summary>Stood down on this context (program or target): the TAA jitter gates on it, so a frame with no
    /// velocity target is never jittered without an accumulator.</summary>
    internal static bool Failed => _failed;

    internal static void Attach(ExtenderLog log) => _log = log;

    /// <summary>Context loss: the names are gone, and so is the history they were built against.</summary>
    internal static void Forget()
    { _fbo = 0; _tex = 0; _prog = 0; _w = 0; _h = 0; _built = false; _failed = false; Revoke(); }

    /// <summary>World edge (area transition): the previous camera is another area's. The next frame paints
    /// zero rather than a jump the length of the load screen.</summary>
    internal static void Revoke() { _hasPrev = false; _lastSeq = -1; _pendingSeq = -1; Painted = false; }

    /// <summary>The pass. <paramref name="vp"/> is this pass's JITTERED view-projection (what the depth was
    /// written with), <paramref name="vpClean"/> the unjittered one that becomes next frame's history, and
    /// (<paramref name="jx"/>, <paramref name="jy"/>) the jitter in NDC. Restores the frame's framebuffer and
    /// the viewport it was given.</summary>
    internal static void Run(uint depthTex, Matrix4x4* vp, Matrix4x4* vpClean, float jx, float jy,
                             int* viewport, uint restoreFbo, int frameSeq)
    {
        Painted = false;
        if (_failed || depthTex == 0) return;
        if (!_built) EnsureProgram();
        if (_prog == 0 || !EnsureTarget(viewport[2], viewport[3])) return;
        if (!_hasPrev) { _prevVp = *vpClean; _hasPrev = true; }
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
        Gl.Uniform2f(_locJitter, jx, jy);
        Gl.DrawArrays(Gl.TRIANGLES, 0, 3);
        Gl.BindTexture(Gl.TEXTURE_2D, 0);
        Gl.Enable(Gl.DEPTH_TEST); Gl.DepthMask(1);
        Gl.BindVertexArray(0);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, restoreFbo);
        Gl.Viewport(viewport[0], viewport[1], viewport[2], viewport[3]);
        // Once per FRAME, not per pass: a second armed pass in the same frame (a reflection subframe) must
        // not make its own camera the next frame's history. The mover sweep that follows reads PrevVp, so
        // the roll waits for EndMovers.
        _pendingPrev = *vpClean; _pendingSeq = frameSeq;
        Painted = true;
        _runs++;
    }

    private static Matrix4x4 _pendingPrev;
    private static int _pendingSeq = -1;

    /// <summary>#392: open the velocity target for the mover sweep, with the frame depth attached so LEQUAL
    /// keeps every mover fragment on its own surface. False when there is no target to draw into.</summary>
    internal static bool BeginMovers(uint depthTex, int* viewport)
    {
        if (!Painted || _tex == 0 || depthTex == 0) return false;
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _fbo);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.DEPTH_ATTACHMENT, Gl.TEXTURE_2D, depthTex, 0);
        Gl.Viewport(0, 0, _w, _h);
        Gl.Enable(Gl.DEPTH_TEST); Gl.DepthFunc(Gl.LEQUAL); Gl.DepthMask(0);
        Gl.Disable(Gl.BLEND);
        return true;
    }

    /// <summary>...and close it: the depth comes off (the fullscreen pass wants a colour-only target) and the
    /// caller's framebuffer and viewport come back. The caller rolls the history (<see cref="RollHistory"/>)
    /// on every path, movers or none.</summary>
    internal static void EndMovers(uint restoreFbo, int* viewport)
    {
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.DEPTH_ATTACHMENT, Gl.TEXTURE_2D, 0, 0);
        Gl.DepthMask(1); Gl.DepthFunc(Gl.LEQUAL);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, restoreFbo);
        Gl.Viewport(viewport[0], viewport[1], viewport[2], viewport[3]);
    }

    /// <summary>Adopt this frame's clean view-projection as the next frame's history, once per frame.</summary>
    internal static void RollHistory()
    {
        if (_pendingSeq >= 0 && _pendingSeq != _lastSeq) { _prevVp = _pendingPrev; _lastSeq = _pendingSeq; }
        _pendingSeq = -1;
    }

    internal static string Stats(int frames)
    {
        int n = _runs; _runs = 0;
        return _failed ? "motion STOOD DOWN" : n == 0 ? "motion idle" : $"motion {(float)n / MathF.Max(frames, 1):F2} runs/frame at {_w}x{_h}" + AlloyRenderer.MoverStats(frames);
    }

    private static bool EnsureTarget(int w, int h)
    {
        if (_tex != 0 && w == _w && h == _h) return true;
        if (w <= 0 || h <= 0) return false;
        if (_fbo == 0) { uint f; Gl.GenFramebuffers(1, &f); _fbo = f; }
        RenderTarget.Create(ref _tex, Gl.RG16F, w, h, Gl.NEAREST);
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
        _prog = GlProgram.Graphics(PostChain.FullscreenVs, CameraFs, _log, "MotionVectors");
        if (_prog == 0) { _failed = true; return; }
        _locDepth = GlProgram.Loc(_prog, "uDepth"); _locInvVp = GlProgram.Loc(_prog, "uInvVP");
        _locPrevVp = GlProgram.Loc(_prog, "uPrevVP"); _locSize = GlProgram.Loc(_prog, "uSize");
        _locJitter = GlProgram.Loc(_prog, "uJitter");
    }

    /// <summary>Depth to NDC (default depth range, so z = 2d-1), through the inverse of this frame's
    /// view-projection and forward through the previous one. The world point stays homogeneous -- the
    /// previous clip w absorbs whatever w the inverse left -- and one divide gives the previous NDC.</summary>
    internal static string CameraFs => ShaderSource.Get("cx_motioncam_f.glsl");

    /// <summary>Off-device: the stage text carries its convention and its bounds, and the history edge
    /// resets.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        check.Expect(CameraFs.Contains("prev.xy / prev.w - (ndc.xy - uJitter)"), "motion is previous minus current (FSR2's convention), the current unjittered");
        check.Expect(AlloyRenderer.MotionFs.Contains("vPrev.xy / vPrev.w - (vCur.xy / vCur.w - uJitter)") && AlloyRenderer.MotionVs.Contains(AlloyRenderer.ClipExpr)
          && AlloyRenderer.MotionVs.Contains("invariant gl_Position;") && AlloyRenderer.MotionVs.Contains("uPrevVP * d.prev * vec4(p, 1.0)"),
          "the mover sweep (#392) shares the convention, and its clip position is the prepass's exact text");
        check.Expect(AlloyRenderer.MotionVs.Contains("if (bb >= 0) gl_Position = uVP * d.model * pos;")
          && AlloyRenderer.MotionVs.Contains($"binding = {AlloyRenderer.BindBonesPrev}")
          && AlloyRenderer.MotionVs.Contains("uPrevVP * d.prev * (CxPrevSkinMat(bb) * vec4(aPos, 1.0))"),
          "a skinned mover (CX-26) takes the prepass's skinned clip text and last frame's palette");
        check.Expect(AlloyRenderer.MotionVs.Contains($"binding = {AlloyRenderer.BindDanglyPrev}")
          && AlloyRenderer.MotionVs.Contains("int k = gl_VertexID - gl_BaseVertex;") && AlloyRenderer.MotionVs.Contains("k < uDangly.y"),
          "a dangly mover (CX-26) reads its own vertex's pre-solve position, bounded by the snapshot");
        check.Expect(ShaderContract.RecordSafe(AlloyRenderer.MotionVs) && AlloyRenderer.PrevFloat == 44 && AlloyRenderer.DrawFloats == 72,
          "the mover stage declares the 72-float record, prev at float 44");
        _pendingPrev = Matrix4x4.CreateTranslation(1f, 0f, 0f); _pendingSeq = 3; _lastSeq = 2;
        RollHistory();
        check.Expect(_prevVp.M41 == 1f && _lastSeq == 3, "the history rolls once the frame's consumers are done");
        _pendingPrev = Matrix4x4.CreateTranslation(2f, 0f, 0f); _pendingSeq = 3;
        RollHistory();
        check.Expect(_prevVp.M41 == 1f, "...and a second pass in the same frame does not roll it again");
        check.Expect(CameraFs.Contains("abs(prev.w) < 1e-6") && CameraFs.Contains("isnan(v)") && CameraFs.Contains("clamp(v, -uSize, uSize)"),
          "no NaN and no unbounded value leaves the stage");
        check.Expect(CameraFs.Contains("d * 2.0 - 1.0"), "depth decodes for the default depth range");
        _hasPrev = true; _lastSeq = 7;
        Revoke();
        check.Expect(!_hasPrev && _lastSeq == -1, "Revoke drops the previous camera");
        log.Info(check.Ok ? "MotionVectors.SelfCheck: passed" : "MotionVectors.SelfCheck: FAILED");
        return check.Ok;
    }
}
