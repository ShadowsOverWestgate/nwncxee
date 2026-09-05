using System;
using System.Numerics;
using Sowx.Framework;
using Sowx.Graphics;

namespace Sowx.Alloy;

/// <summary>#392: object motion vectors for matrix movers. After <see cref="MotionVectors"/> has painted the
/// camera field, every record whose model matrix changed THIS frame is drawn once more into the velocity
/// target, depth-tested LEQUAL against the frame depth it already wrote, with the difference of its current
/// and previous clip positions -- so a door swinging under a still camera carries its own motion and the
/// static world around it keeps the camera-only estimate. Rigid opaque movers only (the staging walk picks
/// them, <c>NoteMoverDraw</c>); skinned, dangly, grass, water, particles and every blended class stay on the
/// camera path by design.</summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>Draw indices of this pass's movers, serial-appended by the staging walk. ponytail: a fixed
    /// cap with an overflow counter -- movers are doors and a handful of placeables; a crowd past the cap
    /// falls back to camera-only vectors, which is what every other class gets anyway.</summary>
    private const int MaxMoverDraws = 1024;
    private static readonly int[] _mvDraw = new int[MaxMoverDraws];
    private static int _mvN, _mvOverflow, _mvDrawn;
    private static uint _progMotion;
    private static int _mvVp, _mvPrevVp, _mvJitter, _mvSize, _mvBase;
    private static bool _mvBuilt, _mvFailed;

    private static void NoteMoverDraw(int n)
    {
        if (_mvN < MaxMoverDraws) _mvDraw[_mvN++] = n; else _mvOverflow++;
    }

    /// <summary>The mover sweep, inside the motion bracket. Reads the STAGED slot buffer, so it must run
    /// where <c>BindStagedSlots</c> has (the prepass hands it back). Leaves the framebuffer and viewport as the
    /// caller had them; program, VAO, cull and blend are the next pass's PassState to set.</summary>
    private static void MoverMotionPass(Matrix4x4* vp, float jx, float jy, int* viewport)
    {
        int n = _mvN; _mvN = 0;
        try
        {
            if (n == 0 || _mvFailed) return;
            if (!_mvBuilt) EnsureMotionProgram();
            if (_progMotion == 0 || !MotionVectors.BeginMovers(PostChain.DepthTex, viewport)) return;
            Sweep(n, vp, jx, jy, viewport);
            MotionVectors.EndMovers(PostChain.Fbo, viewport);
        }
        // Every exit rolls the camera history -- with no movers on screen too, or the field would compare
        // every frame against the first one.
        finally { MotionVectors.RollHistory(); }
    }

    private static void Sweep(int n, Matrix4x4* vp, float jx, float jy, int* viewport)
    {
        Matrix4x4 prevVp = MotionVectors.PrevVp;
        Gl.UseProgram(_progMotion);
        Gl.UniformMatrix4fv(_mvVp, 1, 0, (float*)vp);
        Gl.UniformMatrix4fv(_mvPrevVp, 1, 0, (float*)&prevVp);
        Gl.Uniform2f(_mvJitter, jx, jy);
        Gl.Uniform2f(_mvSize, viewport[2], viewport[3]);
        uint boundVao = 0; int boundCull = -1;
        fixed (int* counts = _dCount, bases = _dBase)
        fixed (nint* offs = _dOff)
        for (int k = 0; k < n; k++)
        {
            int d = _mvDraw[k], i = _dSlot[d];
            uint vao = VaoName(_oVao[i], _oCls[i]);
            if (vao == 0) continue;
            if (vao != boundVao) { Gl.BindVertexArray(vao); boundVao = vao; }
            int wantCull = _oNoCull[i] ? 0 : 1;
            if (wantCull != boundCull)
            {
                boundCull = wantCull;
                if (wantCull != 0) { Gl.Enable(Gl.CULL_FACE); Gl.CullFace(Gl.BACK); } else Gl.Disable(Gl.CULL_FACE);
            }
            Gl.Uniform1i(_mvBase, d);
            Gl.MultiDrawElementsBaseVertex(_primTable[_oPrim[i]], counts + d, GlUnsignedShort, (void**)(offs + d), 1, bases + d);
            _mvDrawn++;
        }
        Gl.BindVertexArray(0);
    }

    private static void EnsureMotionProgram()
    {
        _mvBuilt = true;
        _progMotion = GlProgram.Graphics(MotionVs, MotionFs, _log, "MoverMotion");
        if (_progMotion == 0) { _mvFailed = true; _log?.Info("MoverMotion: the program did not build -- movers keep camera-only vectors"); return; }
        _mvVp = GlProgram.Loc(_progMotion, "uVP"); _mvPrevVp = GlProgram.Loc(_progMotion, "uPrevVP");
        _mvJitter = GlProgram.Loc(_progMotion, "uJitter"); _mvSize = GlProgram.Loc(_progMotion, "uSize");
        _mvBase = GlProgram.Loc(_progMotion, "uDrawBase");
    }

    /// <summary>The [alloy] period term for the sweep; resets the counters.</summary>
    internal static string MoverStats(int frames)
    {
        int d = _mvDrawn, o = _mvOverflow; _mvDrawn = 0; _mvOverflow = 0;
        return _mvFailed ? "movers FAILED" : d == 0 ? "" : $", {(float)d / MathF.Max(frames, 1):F1} mover draws/frame" + (o > 0 ? $" ({o} past the cap)" : "");
    }

    /// <summary>Context loss: the program name is gone.</summary>
    private static void MotionContextLost() { _progMotion = 0; _mvBuilt = false; _mvFailed = false; _mvN = 0; }

    /// <summary>The mover vertex stage. <c>gl_Position</c> is the prepass's exact <see cref="ClipExpr"/> text
    /// under <c>invariant</c>, so LEQUAL against the depth the mover already wrote passes on every fragment;
    /// the previous clip position rides through the record's <c>prev</c> and last frame's view-projection.</summary>
    internal const string MotionVs = """
        #version 460 core
        layout(location = 0) in vec3 aPos;

        """ + DrawBufDecl + """
        uniform mat4 uVP;
        uniform mat4 uPrevVP;    // last frame's view-projection, unjittered
        uniform int uDrawBase;
        invariant gl_Position;
        out vec4 vCur;
        out vec4 vPrev;
        void main()
        {
            SowxDraw d = uDraw[SowxSlot(uDrawBase + gl_DrawID)];
            gl_Position = uVP * d.model * vec4(aPos, 1.0);
            vCur = gl_Position;
            vPrev = uPrevVP * d.prev * vec4(aPos, 1.0);
        }
        """;

    /// <summary>The same convention as <see cref="MotionVectors.CameraFs"/>: prev minus current, render pixels,
    /// the current position unjittered, bounded and never NaN.</summary>
    internal const string MotionFs = """
        #version 460 core
        in vec4 vCur;
        in vec4 vPrev;
        out vec2 frag;
        uniform vec2 uJitter;    // this frame's sub-pixel jitter, NDC
        uniform vec2 uSize;      // the render size, pixels
        void main()
        {
            if (abs(vPrev.w) < 1e-6 || abs(vCur.w) < 1e-6) { frag = vec2(0.0); return; }
            vec2 v = (vPrev.xy / vPrev.w - (vCur.xy / vCur.w - uJitter)) * 0.5 * uSize;
            if (any(isnan(v)) || any(isinf(v))) v = vec2(0.0);
            frag = clamp(v, -uSize, uSize);
        }
        """;
}
