using System;

namespace CX.Alloy;

/// <summary>What is left of CX-106's port of <c>AnimateHierarchy</c> @0x499990: the morph-frame selection
/// out of <c>MdlNodeAnimMesh::InternalControl</c> @0x499320.
/// <para>CX-188: <c>Attach</c> and <c>Walk</c> -- the walk itself, the seven
/// <c>MdlNode*::InternalControl</c> bodies it dispatched through <see cref="CX.Framework.EngineLayout.MdlNodeControl"/>, the
/// <c>AnimateStaticLight</c> announcement and the vertex morph -- had no caller: they applied one engine
/// <c>AnimRun</c> to an engine part tree, and in DB mode <c>DbAnim.Pose</c> poses the database's own records
/// (no pointer compare, no pairing probe). <see cref="Frames"/> survives because it is the one piece of that
/// control flow <c>DbAnim</c> still has to agree with, and <c>AnimMathCheck</c> pins it.</para></summary>
internal static class AnimHierarchy
{
    /// <summary>
    /// Which morph frame each of the two pools uses, and whether it lerps into the next one.
    /// <para><b>The frame index is SHARED and the POSITION pool clamps it in place</b> before the UV pool
    /// reads it (<c>cmovge r13d,ecx</c> @0x49975e and <c>xor r13d,r13d</c> @0x499940, both ahead of the UV
    /// decision at 0x49986f). Clamping each pool against its own frame count independently -- which is what
    /// the code looks like it should do -- extrapolates the UVs where the engine holds a key and holds them
    /// where the engine extrapolates. It only shows on a node whose two pools have different frame counts,
    /// which is why this is its own function with its own check.</para>
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
