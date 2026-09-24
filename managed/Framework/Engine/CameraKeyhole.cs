using System;
using CX.Framework;

namespace CX.Engine;

/// <summary>The interior of the camera's keyhole BLOCK. <see cref="EngineLayout.CamKeyholeState"/> anchors 24
/// bytes -- the enable byte, three of padding, the aperture plane and the height cut -- and no engine field
/// names what sits inside it, so the two halves are concepts on the view rather than rows of their own.
/// </summary>
public readonly unsafe partial struct Camera
{
    /// <summary>#330: the aperture plane <c>Camera::GetCanKeyholeDissolve</c> @0x4a6f70 tests a box against,
    /// as the four floats it reads them as.</summary>
    public Span<float> KeyholePlane => new((void*)(Ptr + EngineLayout.CamKeyholeState + PlaneInBlock), 4);
    /// <summary>#330: the height cut that follows the plane -- geometry above it never dissolves.</summary>
    public ref float KeyholeCut => ref *(float*)(Ptr + EngineLayout.CamKeyholeState + CutInBlock);
    /// <summary>#330: the whole block as the 8-byte words a dirty-compare takes it in, rather than as the
    /// fields above -- the enable byte and its padding ride along, and comparing more is only more
    /// conservative.</summary>
    public Span<ulong> KeyholeWords => new((void*)(Ptr + EngineLayout.CamKeyholeState), KeyholeWordCount);
    /// <summary>Words in <see cref="KeyholeWords"/>: the 24-byte block, eight bytes at a time.</summary>
    public const int KeyholeWordCount = 3;

    /// <summary>Byte offsets INSIDE the keyhole block: the plane starts past the enable byte and its three
    /// bytes of padding (+0x4), the height cut past the plane's four floats (+0x14).</summary>
    private const int PlaneInBlock = 4, CutInBlock = 20;
}
