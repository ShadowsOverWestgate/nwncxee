using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>Grass's own motion vectors: the field the camera pass painted over a blade's pixels is the
/// GROUND's -- grass is displaced by wind, not by a matrix -- so both paths are re-issued into the velocity
/// target with last frame's sway, and TAA stops smearing it across the history.</summary>
internal static unsafe partial class GrassField
{
    /// <summary>What a LATE pass needs to re-issue this pass's draws exactly as they were drawn. Fields are
    /// rewritten in place by <see cref="Issue"/> -- no allocation on the frame path -- and <see cref="Gen"/>
    /// is what says they describe THIS pass rather than an older one.</summary>
    private sealed class GrassIssueState
    {
        public Vector3 Eye;
        /// <summary>The ladder's two radii, as <see cref="Issue"/> derived them.</summary>
        public float Near, Far;
        public int Gen = -1;
        /// <summary>Did the colour pass get its ground snapshot? The blade height reads it.</summary>
        public bool Ground;
        /// <summary>The atlas texture id per material, from the walk <see cref="PrepareMotion"/> makes
        /// before the velocity target is opened.</summary>
        public readonly int[] Tids = new int[MaxGrassMaterials];
    }

    private static GrassIssueState _issued = new();

    /// <summary>Note this pass's draw parameters for the velocity sweep.</summary>
    private static void NoteIssued(Vector3 eye, float nearDist, float farDist, bool ground)
    {
        _issued.Eye = eye; _issued.Near = nearDist; _issued.Far = farDist; _issued.Ground = ground;
        _issued.Gen = AlloyRenderer.PassGen;
    }

    /// <summary>Has this pass drawn grass that the sweep can re-issue? Read by the sweep before it opens the
    /// velocity target at all.</summary>
    internal static bool HasMotion
        => _issued.Gen == AlloyRenderer.PassGen && !_off && Grass.Enabled.V && !_down.Failed && _gl.Vao != 0
        && (BladeMotion || CardMotion);

    private static bool BladeMotion => _bladeNear + _bladeFar > 0 && _gl.ProgBladeMotion != 0;
    private static bool CardMotion => _slotCount > 0 && _gl.ProgMotion != 0;

    /// <summary>The engine texture walk, BEFORE the caller opens the velocity target: AurTextureGet's create
    /// path can touch GL and rebind buffers, and it must not do that between that bind and the draws.</summary>
    internal static void PrepareMotion()
    {
        if (!HasMotion) return;
        bool* live = stackalloc bool[MaxGrassMaterials];
        fixed (int* tids = _issued.Tids) ResolveTextures(tids, live);
        EnsureSamplers();
    }

    /// <summary>Re-issue this pass's blades and cards into the velocity target the caller has opened, against
    /// the depth the colour pass wrote: every fragment that survives the same coverage decision paints its own
    /// vector over the camera field. The caller owns the framebuffer, the depth test and the viewport.</summary>
    internal static void DrawMotion(Matrix4x4* vp, Matrix4x4* prevVp, int prevClock, float jx, float jy, int* viewport)
    {
        if (!HasMotion) return;
        float* pw = stackalloc float[3] { _pushOriginX, _pushOriginY, _pushPerUnit / PushRes };
        Vector3 eye = _issued.Eye;
        Gl.BindVertexArray(_gl.Vao);
        Gl.BindBuffer(Gl.DRAW_INDIRECT_BUFFER, _gl.CmdBuf);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindMats, _gl.MatBuf);
        // The push field's image unit is the post stack's HDR unit (PushUnit == PostHdr), and by this point
        // in the frame it holds a picture; the blades would be displaced by it. Same reason DrawShadows does.
        Gl.ActiveTexture(Gl.TEXTURE0 + PushUnit);
        Gl.BindTexture(Gl.TEXTURE_2D, _gl.PushTex);
        Gl.ActiveTexture(Gl.TEXTURE0);
        new PassState(depth: true, depthWrite: false, cull: false).Apply();
        Gl.ColorMask(1, 1, 1, 1);
        if (BladeMotion)
        {
            Gl.UseProgram(_gl.ProgBladeMotion);
            SharedUniforms(in _gl.LocBM, in _gl.FeatLocBM, vp, &eye, _issued.Near, _issued.Far, false, pw, viewport, vlit: false);
            MotionUniforms(in _gl.LocBM, prevVp, prevClock, jx, jy, viewport);
            // the blade's HEIGHT reads this one: the sweep's geometry has to be the geometry that was drawn
            Gl.Uniform1i(_gl.LocBM.GroundOn, _issued.Ground ? 1 : 0);
            DrawBlades(in _gl.LocBM);
        }
        if (CardMotion)
        {
            Gl.UseProgram(_gl.ProgMotion);
            SharedUniforms(in _gl.LocM, in _gl.FeatLocM, vp, &eye, _issued.Near, _issued.Far, false, pw, viewport, vlit: false);
            MotionUniforms(in _gl.LocM, prevVp, prevClock, jx, jy, viewport);
            // The velocity target has one sample, so the coverage decision takes its single-sample arm
            // whatever the frame target has: the opaque core, dithered out through the fade band.
            Gl.Uniform1i(_gl.LocM.SoftEdge, 0);
            fixed (int* tids = _issued.Tids) DrawSlots(tids, in _gl.LocM, depth: true);
        }
        if (_gl.SampMip != 0) Gl.BindSampler(0, 0);
        Gl.BindBuffer(Gl.DRAW_INDIRECT_BUFFER, 0);
        Gl.BindVertexArray(0);
    }

    /// <summary>The velocity arm's own four: last frame's camera and clock, and the jitter and render size
    /// the shared convention is written in.</summary>
    private static void MotionUniforms(in Loc loc, Matrix4x4* prevVp, int prevClock, float jx, float jy, int* viewport)
    {
        Gl.UniformMatrix4fv(loc.PrevVp, 1, 0, (float*)prevVp);
        Gl.Uniform1i(loc.PrevTime, prevClock);
        Gl.Uniform2f(loc.Jitter, jx, jy);
        Gl.Uniform2f(loc.MSize, viewport[2], viewport[3]);
    }
}
