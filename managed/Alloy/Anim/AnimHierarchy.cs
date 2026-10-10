using System;

namespace CX.Alloy;

/// <summary>Morph-frame selection, ported out of the engine's <c>AnimateHierarchy</c> @0x499990: the one
/// piece of that control flow <c>DbAnim.Pose</c> still has to agree with, pinned by <c>AnimMathCheck</c>.</summary>
internal static class AnimHierarchy
{
    /// <summary>
    /// Which morph frame each of the two pools uses, and whether it lerps into the next one.
    /// <para>The frame index is SHARED and the position pool clamps it in place before the UV pool reads it;
    /// clamping each pool against its own frame count instead extrapolates the UVs where the engine holds a
    /// key, visible only on a node whose two pools have different frame counts.</para>
    /// </summary>
    internal static void Frames(ref int frame, int vf, int tf, out bool lerpPos, out bool lerpUv, out int uvFrame)
    {
        lerpPos = false;
        if (vf != 0)
        {
            if (frame < 0) frame = 0;
            else if (frame < vf - 1) lerpPos = true;
            else if (frame >= vf) frame = vf - 1;
        }
        // A frame still negative here means the positions never ran (no morph vertex frames at all); the
        // engine forces it to 0 and takes the exact key (@0x49987b).
        int uf = frame < 0 ? 0 : frame;
        lerpUv = frame >= 0 && uf < tf - 1;
        uvFrame = lerpUv ? uf : Math.Min(uf, tf - 1);
    }
}
