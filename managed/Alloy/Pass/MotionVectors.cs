using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>
/// Camera motion vectors: once per frame, after the opaque depth is final, every render pixel is reprojected
/// through the PREVIOUS frame's view-projection into an RG16F target -- exact for every pixel whose model
/// matrix did not move, and corrected over by the mover, skinmesh and dangly sweeps for the rest. Convention:
/// <c>prev - cur</c>, in RENDER pixels, GL y-up, UNJITTERED, so a still camera under TAA paints exactly zero.
/// </summary>
internal static unsafe class MotionVectors
{

    private static ExtenderLog? _log;

    /// <summary>Everything the GL context owns: context loss replaces it, so no name from the dead context
    /// survives as a field nobody remembered to zero.</summary>
    private sealed class MotionGlState
    {
        public uint Fbo, Tex, Prog;
        public int W, H;
        public int LocDepth, LocInvVp, LocPrevVp, LocSize, LocJitter;
        public bool Built, Failed;
    }

    /// <summary>The camera history, which the WORLD owns: an area transition replaces it, so the next frame
    /// paints zero rather than a jump the length of the load screen. Context loss replaces it as well -- the
    /// target the history was reprojected against is gone.</summary>
    private sealed class MotionHistoryState
    {
        /// <summary>Last frame's UNJITTERED view-projection, and whether one has been taken at all.</summary>
        public Matrix4x4 PrevVp;
        public bool HasPrev;
        public int LastSeq = -1;
        /// <summary>This frame's clean camera, waiting for the frame's consumers: adopted once, by
        /// <see cref="RollHistory"/>, so a second armed pass in the same frame cannot roll it again.</summary>
        public Matrix4x4 PendingPrev;
        public int PendingSeq = -1;
    }

    private static MotionGlState _gl = new();
    private static MotionHistoryState _hist = new();
    private static int _runs;
    /// <summary>The clean camera of the last frame's first run, for the period's camera-steadiness term: how many
    /// frames the view-projection changed at all, and the largest element change.</summary>
    private static Matrix4x4 _camLast;
    private static int _camSeq = -1, _camMoved, _camStill;
    private static float _camMax;

    /// <summary>The velocity target, 0 until the pass has run once at this size.</summary>
    internal static uint Tex => _gl.Tex;
    /// <summary>Last frame's UNJITTERED view-projection, for the mover sweep -- valid after Run.</summary>
    internal static Matrix4x4 PrevVp => _hist.PrevVp;
    /// <summary>Did the pass paint this frame? False until Run has produced a target the consumers may read.</summary>
    internal static bool Painted { get; private set; }
    /// <summary>Stood down on this context (program or target): the TAA jitter gates on it, so a frame with no
    /// velocity target is never jittered without an accumulator.</summary>
    internal static bool Failed => _gl.Failed;

    internal static void Attach(ExtenderLog log) => _log = log;

    /// <summary>Context loss: the names are gone, and so is the history they were built against.</summary>
    internal static void Forget()
    { _gl = new MotionGlState(); Revoke(); }

    /// <summary>World edge (area transition): the previous camera is another area's. The next frame paints
    /// zero rather than a jump the length of the load screen.</summary>
    internal static void Revoke() { _hist = new MotionHistoryState(); Painted = false; }

    /// <summary>The pass. <paramref name="vp"/> is this pass's JITTERED view-projection (what the depth was
    /// written with), <paramref name="vpClean"/> the unjittered one that becomes next frame's history, and
    /// (<paramref name="jx"/>, <paramref name="jy"/>) the jitter in NDC. Restores the frame's framebuffer and
    /// the viewport it was given.</summary>
    internal static void Run(uint depthTex, Matrix4x4* vp, Matrix4x4* vpClean, float jx, float jy,
                             int* viewport, uint restoreFbo, int frameSeq)
    {
        Painted = false;
        if (_gl.Failed || depthTex == 0) return;
        if (!_gl.Built) EnsureProgram();
        if (_gl.Prog == 0 || !EnsureTarget(viewport[2], viewport[3])) return;
        if (!_hist.HasPrev) { _hist.PrevVp = *vpClean; _hist.HasPrev = true; }
        if (frameSeq != _camSeq) NoteCamera(vpClean, frameSeq);
        if (!Matrix4x4.Invert(*vp, out Matrix4x4 inv)) return;
        Matrix4x4 prev = _hist.PrevVp;

        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _gl.Fbo);
        Gl.Viewport(0, 0, _gl.W, _gl.H);
        Gl.BindVertexArray(PostChain.FullscreenVao);
        new PassState(depth: false, depthWrite: false).Apply();
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.BindTexture(Gl.TEXTURE_2D, depthTex);
        Gl.UseProgram(_gl.Prog);
        Gl.Uniform1i(_gl.LocDepth, 0);
        // raw row-major bytes, read column-major by GLSL: the transpose, so uInvVP * ndc is exact (Frame.cs)
        Gl.UniformMatrix4fv(_gl.LocInvVp, 1, 0, (float*)&inv);
        Gl.UniformMatrix4fv(_gl.LocPrevVp, 1, 0, (float*)&prev);
        Gl.Uniform2f(_gl.LocSize, _gl.W, _gl.H);
        Gl.Uniform2f(_gl.LocJitter, jx, jy);
        Gl.DrawArrays(Gl.TRIANGLES, 0, 3);
        Gl.BindTexture(Gl.TEXTURE_2D, 0);
        Gl.Enable(Gl.DEPTH_TEST); Gl.DepthMask(1);
        Gl.BindVertexArray(0);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, restoreFbo);
        Gl.Viewport(viewport[0], viewport[1], viewport[2], viewport[3]);
        // Once per FRAME, not per pass: a second armed pass in the same frame (a reflection subframe) must
        // not make its own camera the next frame's history. The mover sweep that follows reads PrevVp, so
        // the roll waits for EndMovers.
        _hist.PendingPrev = *vpClean; _hist.PendingSeq = frameSeq;
        Painted = true;
        _runs++;
    }

    /// <summary>Open the velocity target for the mover sweep, with the frame depth attached so LEQUAL
    /// keeps every mover fragment on its own surface. False when there is no target to draw into.</summary>
    internal static bool BeginMovers(uint depthTex, int* viewport)
    {
        if (!Painted || _gl.Tex == 0 || depthTex == 0) return false;
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _gl.Fbo);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.DEPTH_ATTACHMENT, Gl.TEXTURE_2D, depthTex, 0);
        Gl.Viewport(0, 0, _gl.W, _gl.H);
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
        if (_hist.PendingSeq >= 0 && _hist.PendingSeq != _hist.LastSeq) { _hist.PrevVp = _hist.PendingPrev; _hist.LastSeq = _hist.PendingSeq; }
        _hist.PendingSeq = -1;
    }

    private static void NoteCamera(Matrix4x4* vpClean, int frameSeq)
    {
        if (_camSeq >= 0)
        {
            float m = 0f;
            float* a = (float*)vpClean;
            fixed (Matrix4x4* pb = &_camLast) { float* b = (float*)pb; for (int k = 0; k < 16; k++) m = MathF.Max(m, MathF.Abs(a[k] - b[k])); }
            if (m > 1e-6f) { _camMoved++; _camMax = MathF.Max(_camMax, m); } else _camStill++;
        }
        _camLast = *vpClean; _camSeq = frameSeq;
    }

    internal static string Stats(int frames)
    {
        int n = _runs; _runs = 0;
        string cam = $", camera moved {_camMoved}/{_camMoved + _camStill} frames (max element step {_camMax:G3})";
        _camMoved = 0; _camStill = 0; _camMax = 0f;
        return _gl.Failed ? "motion STOOD DOWN" : n == 0 ? "motion idle" : $"motion {(float)n / MathF.Max(frames, 1):F2} runs/frame at {_gl.W}x{_gl.H}" + cam + AlloyRenderer.MoverStats(frames);
    }

    private static bool EnsureTarget(int w, int h)
    {
        if (_gl.Tex != 0 && w == _gl.W && h == _gl.H) return true;
        if (w <= 0 || h <= 0) return false;
        if (_gl.Fbo == 0) { uint f; Gl.GenFramebuffers(1, &f); _gl.Fbo = f; }
        RenderTarget.Create(ref _gl.Tex, Gl.RG16F, w, h, Gl.NEAREST);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _gl.Fbo);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, _gl.Tex, 0);
        uint status = Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER);
        if (status != Gl.FRAMEBUFFER_COMPLETE)
        {
            _gl.Failed = true;
            _log?.Info($"MotionVectors: the RG16F target is incomplete (0x{status:x}) -- the pass stands down on this context");
            return false;
        }
        _gl.W = w; _gl.H = h;
        _log?.Info($"MotionVectors: velocity target {w}x{h} RG16F");
        return true;
    }

    private static void EnsureProgram()
    {
        _gl.Built = true;
        _gl.Prog = GlProgram.Graphics(PostChain.FullscreenVs, CameraFs, _log, "MotionVectors");
        if (_gl.Prog == 0) { _gl.Failed = true; return; }
        _gl.LocDepth = GlProgram.Loc(_gl.Prog, "uDepth"); _gl.LocInvVp = GlProgram.Loc(_gl.Prog, "uInvVP");
        _gl.LocPrevVp = GlProgram.Loc(_gl.Prog, "uPrevVP"); _gl.LocSize = GlProgram.Loc(_gl.Prog, "uSize");
        _gl.LocJitter = GlProgram.Loc(_gl.Prog, "uJitter");
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
        check.Expect(ShaderContract.RecordSafe(AlloyRenderer.MotionVs),
          "the mover stage's CxDraw declaration does not add up to the record stride C# stages");
        _hist.PendingPrev = Matrix4x4.CreateTranslation(1f, 0f, 0f); _hist.PendingSeq = 3; _hist.LastSeq = 2;
        RollHistory();
        check.Expect(_hist.PrevVp.M41 == 1f && _hist.LastSeq == 3, "the history rolls once the frame's consumers are done");
        _hist.PendingPrev = Matrix4x4.CreateTranslation(2f, 0f, 0f); _hist.PendingSeq = 3;
        RollHistory();
        check.Expect(_hist.PrevVp.M41 == 1f, "...and a second pass in the same frame does not roll it again");
        check.Expect(CameraFs.Contains("abs(prev.w) < 1e-6") && CameraFs.Contains("isnan(v)") && CameraFs.Contains("clamp(v, -uSize, uSize)"),
          "no NaN and no unbounded value leaves the stage");
        check.Expect(CameraFs.Contains("d * 2.0 - 1.0"), "depth decodes for the default depth range");
        _hist.HasPrev = true; _hist.LastSeq = 7;
        Revoke();
        check.Expect(!_hist.HasPrev && _hist.LastSeq == -1, "Revoke drops the previous camera");
        log.Info(check.Ok ? "MotionVectors.SelfCheck: passed" : "MotionVectors.SelfCheck: FAILED");
        return check.Ok;
    }
}
