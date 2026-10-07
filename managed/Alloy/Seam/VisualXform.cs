using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy.Seam;

/// <summary>The <c>CAurObjectVisualTransformData</c> a <c>Gob</c> carries, kept exactly where the engine kept
/// it: EMBEDDED IN THE SHELL at +0x1c0, ten <c>LerpFloat</c> records of 0x40 bytes each, in the order
/// ScaleX, ScaleY, ScaleZ, RotateX, RotateY, RotateZ, TranslateX, TranslateY, TranslateZ, AnimationSpeed
/// (<c>Gob::GetVisualTransformData</c> @0x436e70 is <c>return this + 0x1c0</c>, and the member names come from
/// the server's own GFF writer <c>CNWSObject::SaveVisualTransform</c>; sizeof is 0x280, confirmed by
/// <c>nm --print-size</c> on <c>CAurObjectVisualTransformData::s_IdentityVisualTransformData</c>).
/// <para>Because the record is the engine's byte-for-byte, the seam samples it with the engine's own
/// <c>LerpFloat::Lerped</c> instead of reimplementing a lerp whose internal fields were never decompiled. The
/// game layer reads ScaleY/ScaleZ off this record directly (<c>CNWCObject.c:4929</c>), which is the second
/// reason it has to live at the engine's offset rather than in a column.</para></summary>
public static unsafe class VisualXform
{
    /// <summary>Offset of the record inside the shell, and the stride and count of its <c>LerpFloat</c>s.</summary>
    public static readonly int At = EngineLayout.GobScaleLerp;
    /// <summary>One <c>LerpFloat</c>'s size, and how many the record holds.</summary>
    public static readonly int Stride = EngineLayout.SeamLerpFloatSizeof;
    public const int Count = 10;   // not-an-offset: how many LerpFloats the record holds
    public static int Size => Stride * Count;

    private const int ScaleX = 0, ScaleY = 1, ScaleZ = 2, RotateX = 3, RotateY = 4, RotateZ = 5;
    private const int TranslateX = 6, TranslateY = 7, TranslateZ = 8, AnimationSpeed = 9;

    private static nint _zero;
    private static bool _warned;

    /// <summary>A zeroed record to hand back for an object whose transform was never set: the engine's getter
    /// returns a reference, never null, so a refusal here must still be a readable 0x280 bytes.</summary>
    public static nint Zero
    {
        get
        {
            if (_zero == 0)
            {
                void* b = NativeMemory.AlignedAlloc((nuint)Size, 16);
                if (b != null) { NativeMemory.Fill(b, (nuint)Size, 0); _zero = (nint)b; }
            }
            return _zero;
        }
    }

    /// <summary>Seed the record the way <c>Gob::Gob</c> @0x440a00 does. It is not all zeros: the three SCALE
    /// lerps and ANIMATION SPEED start at 1.0, and only the rotate and translate ones start at 0 --
    /// <c>*(this+0x1c4) = *(this+0x204) = *(this+0x244) = *(this+0x404) = 0x3f8000003f800000</c>, i.e. both
    /// endpoints of each of those four at 1.0.
    /// <para>Leaving them zero made <c>Gob::GetAdjustedScale</c> answer 0 for EVERY object -- which collapsed
    /// every bounding box the game divides by (the main menu's backdrop came out at infinite scale and a NaN
    /// position) and made <c>PlayAnimation</c>'s speed multiplier 0.</para></summary>
    public static void Seed(nint shell)
    {
        if (shell == 0) return;
        foreach (int which in stackalloc[] { ScaleX, ScaleY, ScaleZ, AnimationSpeed })
        {
            float* f = (float*)(shell + At + which * Stride);
            f[1] = 1f;
            f[2] = 1f;
        }
    }

    /// <summary>The record inside <paramref name="shell"/>.</summary>
    public static nint Of(nint shell) => shell == 0 ? Zero : shell + At;

    /// <summary>Copy the caller's record in verbatim and <c>Refresh</c> each of the ten, which is exactly what
    /// <c>Gob::SetVisualTransformData</c> does -- ten 0x40-byte field copies then the refresh.</summary>
    public static void Store(nint shell, nint src)
    {
        if (shell == 0 || src == 0) return;
        Buffer.MemoryCopy((void*)src, (void*)(shell + At), Size, Size);
        if (EngineFn.LerpFloatRefresh == null) { Warn(); return; }
        for (int i = 0; i < Count; i++) EngineFn.LerpFloatRefresh(shell + At + i * Stride);
    }

    private static float Sample(nint shell, int which, float fallback)
    {
        if (shell == 0) return fallback;
        if (EngineFn.LerpFloatLerped == null) { Warn(); return fallback; }
        return EngineFn.LerpFloatLerped(shell + At + which * Stride);
    }

    /// <summary><c>Gob::GetAdjustedScale</c> uses ScaleX ONLY: ScaleY and ScaleZ are read by the game layer,
    /// never folded into the adjusted scale.</summary>
    public static float Scale(nint shell) => Sample(shell, ScaleX, 1f);

    /// <summary>The animation-speed multiplier <c>Gob::PlayAnimation</c> samples.</summary>
    public static float AnimSpeed(nint shell) => Sample(shell, AnimationSpeed, 1f);

    /// <summary>The three rotate angles in the engine's own argument order: <c>YawPitchRoll(RotateX, RotateY,
    /// RotateZ)</c>. False when all three are zero, which is the engine's fast path -- the raw orientation is
    /// then the adjusted one, bit for bit.</summary>
    public static bool Angles(nint shell, out float x, out float y, out float z)
    {
        x = Sample(shell, RotateX, 0f);
        y = Sample(shell, RotateY, 0f);
        z = Sample(shell, RotateZ, 0f);
        return x != 0f || y != 0f || z != 0f;
    }

    /// <summary>The translate offset, which <c>Gob::GetAdjustedPosition</c> rotates by the RAW orientation (not
    /// the adjusted one) before adding to the raw position.</summary>
    public static Vector3 Translate(nint shell)
        => new(Sample(shell, TranslateX, 0f), Sample(shell, TranslateY, 0f), Sample(shell, TranslateZ, 0f));

    /// <summary>The three adjusted getters folded onto (pos, rot, scale): translate rotated by the RAW
    /// orientation, then raw x YawPitchRoll, then scale x ScaleX. <paramref name="lerping"/> while any of the
    /// nine has not reached its end value (<c>LerpFloat</c>+8, the value <see cref="Seed"/> writes as "to").</summary>
    public static void Adjust(nint shell, ref Vector3 pos, ref Quaternion rot, ref float scale, out bool lerping)
    {
        lerping = false;
        if (shell == 0 || EngineFn.LerpFloatLerped == null) return;
        for (int i = 0; i < AnimationSpeed && !lerping; i++)
        {
            nint r = shell + At + i * Stride;
            lerping = EngineFn.LerpFloatLerped(r) != ((float*)r)[2];
        }
        Vector3 t = Translate(shell);
        if (t != Vector3.Zero) pos += Vector3.Transform(t, rot);
        if (Angles(shell, out float x, out float y, out float z)) rot *= Rotation(x, y, z);
        scale *= Scale(shell);
    }

    private static void Warn()
    {
        if (_warned) return;
        _warned = true;
        Facade.Log?.Always("seam: LerpFloat::Lerped/Refresh did not resolve -- visual transforms are read as identity, not faked");
    }

    /// <summary><c>YawPitchRoll(float, float, float)</c> returns a <c>Quaternion</c> by value, so its
    /// declaration differs per OS: SysV hands it back in xmm0:xmm1 with no buffer, Win64 takes a buffer in rcx
    /// and pushes the three floats one register right. The repo's <c>AurXform</c> carries the same split for
    /// the same reason.</summary>
    public static Quaternion Rotation(float x, float y, float z)
    {
        nint fn = Native.Sym(SymbolMap.YawPitchRoll);
        if (fn == 0)
        {
            Warn();
            return Quaternion.Identity;
        }
        if (OperatingSystem.IsWindows())
        {
            EQuat r = default;
            ((delegate* unmanaged<EQuat*, float, float, float, void>)fn)(&r, x, y, z);
            return r.Q;
        }
        return ((delegate* unmanaged<float, float, float, EQuat>)fn)(x, y, z).Q;
    }
}
