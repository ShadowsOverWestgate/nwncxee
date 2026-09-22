using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

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

    /// <summary>#536: a camera CUT -- the camera layer snapping after a stand-down gate lifts, or a mode
    /// switch. Drops the reprojection history so neither the motion vectors nor TAA carry one frame of the
    /// wrong camera. Flag writes only; safe from any point of the frame.</summary>
    public static void CameraCut() { MotionVectors.Revoke(); TaaStage.Reset(); }

    private static void NoteMoverDraw(int n)
    {
        if (_mvN < MaxMoverDraws) _mvDraw[_mvN++] = n; else _mvOverflow++;
    }

    /// <summary>The mover sweep, inside the motion bracket. Reads the STAGED slot buffer, so it must run
    /// where <c>Issue.BindStagedSlots</c> has (the prepass hands it back). Leaves the framebuffer and viewport as the
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
            uint vao = VaoName(_oVao[i] | _oLodBit[i], _oCls[i]);
            if (vao == 0) continue;
            BindBatchState(_oNoCull[i], vao, ref boundCull, ref boundVao);
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
    internal static string MotionVs => ShaderSource.Get("cx_motion_v.glsl");

    /// <summary>The same convention as <see cref="MotionVectors.CameraFs"/>: prev minus current, render pixels,
    /// the current position unjittered, bounded and never NaN.</summary>
    internal static string MotionFs => ShaderSource.Get("cx_motion_f.glsl");
}
