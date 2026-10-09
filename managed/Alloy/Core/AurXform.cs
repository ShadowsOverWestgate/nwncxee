using System;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>
/// The Aurora calls that pass or return a <c>Vector</c> or <c>Quaternion</c> BY VALUE (CX-86's simulation, CX-82's
/// glow), and the two argument lists each of them needs. Same shape and same reason as the deleted <c>EmitterLighting</c>: a 12-byte
/// three-float struct is classified into xmm0 (x, y) + xmm1 (z) by SysV with no hidden buffer, while Win64
/// passes and returns one through a pointer -- so no single declared signature describes them and the
/// generator's <c>sret</c> ordering does not apply either (it assumes both platforms use a buffer).
/// </summary>
/// <remarks>
/// <para>Every one of the three was read off BOTH images rather than derived from the C++ signature.
/// <c>Scene::GetWindVector</c>: Linux @0x460c60 is <c>mov 0x160(%rdi),%rdi</c> + <c>movq (%rsi),%xmm0</c> +
/// <c>movss 0x8(%rsi),%xmm1</c> with dt arriving in xmm0 and no buffer at all, and it tail-jumps, so the
/// result comes back in xmm0/xmm1; Windows rva 0xc9b90 is <c>mov rcx,[rcx+0x160]</c> with the buffer in rdx
/// and the vector's address in r8, i.e. <c>(this, sret, &amp;pos, dt)</c>.
/// <c>WindManager::AddPointSourceWind</c>: Linux @0x491dc0 opens <c>mov %rdi,%rbx</c> and spills xmm0/xmm1 as
/// the vector. <c>Gob::GetPosition</c>: Windows rva 0x48530 is <c>mov eax,[rcx+0xb0]</c> +
/// <c>movsd xmm0,[rcx+0xa8]</c> + <c>movsd [rdx],xmm0</c> -- <c>this</c> in rcx, buffer in rdx.
/// <c>Gob::GetOrientation</c> returns its 16-byte <c>Quaternion</c> the same two ways: Linux @0x4446a0 is two
/// <c>movq</c> loads into xmm0/xmm1 off <c>this</c> in rdi, Windows rva 0x48520 stores through rdx. CX-86 first
/// declared it <c>sret</c> on both, which handed Linux's engine the buffer as <c>this</c>. The two
/// <c>GetAdjusted*</c> getters (CX-82) were read the same way: Linux @0x437d20/@0x439900, Windows rva
/// 0x8e200/0x8e2c0.</para>
/// </remarks>
internal static unsafe class AurXform
{
    /// <summary>A three-float value classified the way SysV classifies it -- xmm0 carries x and y, xmm1
    /// carries z. <c>Vector3</c>'s ABI is the runtime's to choose, so the layout is declared here instead.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct V3 { public float X, Y, Z; }

    private static IntPtr _wind, _gust;

    /// <summary>Both symbols resolved on this client? A caller gates on this the way it gates on any other
    /// engine pointer; the vtable slots behind <see cref="Position"/> are <c>EngineLayout</c>'s own and are
    /// covered by <c>EngineLayout.Require</c>.</summary>
    internal static bool Ready => _wind != IntPtr.Zero && _gust != IntPtr.Zero;

    internal static void Resolve()
    {
        _wind = Native.Sym(SymbolMap.SceneGetWindVector);
        _gust = Native.Sym(SymbolMap.AddPointSourceWind);
    }

    /// <summary>Names whichever of the two did not resolve, for the caller's one stand-down line.</summary>
    internal static string Missing => _wind == IntPtr.Zero ? SymbolMap.SceneGetWindVector : SymbolMap.AddPointSourceWind;

    /// <summary>The wind displacement to add to a particle stepped over <paramref name="dt"/>: global wind
    /// plus every live point source, sampled at <paramref name="pos"/>.
    /// <para>CX-110: <see cref="CX.Alloy.AlloyWind"/> answers this, off its own clock and its own noise field.
    /// <c>view.wind 0</c> sends it back to <c>Scene::GetWindVector</c>, which is what the two declarations
    /// below are still for.</para>
    /// <para><see cref="CX.Alloy.DanglyPhysics"/> holds the same two declarations for the same symbol, for
    /// the same reason; it resolves them in its own <c>Install</c> because it stands down on its own terms.
    /// Two copies of one ABI is the wart #456 named, and the mitigation is this pointer between them.</para>
    /// </summary>
    internal static V3 Wind(IntPtr scene, V3 pos, float dt)
    {
        if (CX.Alloy.AlloyWind.Owned)
        {
            System.Numerics.Vector3 w = CX.Alloy.AlloyWind.Wind(new System.Numerics.Vector3(pos.X, pos.Y, pos.Z), dt);
            return new V3 { X = w.X, Y = w.Y, Z = w.Z };
        }
        if (OperatingSystem.IsWindows())
        {
            V3 r = default;
            ((delegate* unmanaged<IntPtr, V3*, V3*, float, void>)_wind)(scene, &r, &pos, dt);
            return r;
        }
        return ((delegate* unmanaged<IntPtr, V3*, float, V3>)_wind)(scene, &pos, dt);
    }

    /// <summary>A detonation's gust: <c>WindManager::AddPointSourceWind(pos, radius, life, strength)</c>,
    /// with the strength defaulting to 1.0 exactly as <c>ExplosionEmitter::Update</c> @0x4b259e pins it; a moving
    /// gob's gust (<c>Gob::Animate</c> @0x49dc91) passes <see cref="EngineLayout.GobWindStrength"/> instead.
    /// <paramref name="windManager"/> is <see cref="EngineLayout.SceneWindManager"/>'s pointer.
    /// <para>CX-110 corrected the parameter NAMES, not the call: <c>PointSourceWind::PointSourceWind</c>
    /// @0x490410 stores xmm2 at <c>+0x0c</c> (the radius the falloff is measured over), xmm3 at BOTH
    /// <c>+0x10</c> and <c>+0x14</c> (the lifetime and its remaining half) and xmm4 at <c>+0x18</c> (the
    /// strength), on both builds. The engine's own call site passes <c>MdlNodeEmitter+0xa4</c> in the lifetime
    /// slot, which is the slot CX's caller already fills, so the positions were right all along.</para>
    /// <para><see cref="CX.Alloy.AlloyWind"/> hooks this function, so a gust raised here lands in Alloy's list
    /// as well as the engine's without a second call.</para></summary>
    internal static void Gust(IntPtr windManager, V3 pos, float radius, float life, float strength = 1f)
    {
        if (OperatingSystem.IsWindows())
            ((delegate* unmanaged<IntPtr, V3*, float, float, float, void>)_gust)(windManager, &pos, radius, life, strength);
        else
            ((delegate* unmanaged<V3, IntPtr, float, float, float, void>)_gust)(pos, windManager, radius, life, strength);
    }

    /// <summary>A <c>CAurObject</c>'s rotation through its own virtual: <see cref="EngineLayout.GobGetOrientationSlot"/>.</summary>
    internal static Quat Orientation(IntPtr obj) => CallQuat(new CX.Engine.Gob(obj).GetOrientationFn, obj);

    private static Quat CallQuat(IntPtr fn, IntPtr obj)
    {
        if (OperatingSystem.IsWindows())
        {
            Quat r = default;
            ((delegate* unmanaged<IntPtr, Quat*, void>)fn)(obj, &r);
            return r;
        }
        return ((delegate* unmanaged<IntPtr, Quat>)fn)(obj);
    }

    private static V3 CallV3(IntPtr fn, IntPtr obj)
    {
        if (OperatingSystem.IsWindows())
        {
            V3 r = default;
            ((delegate* unmanaged<IntPtr, V3*, void>)fn)(obj, &r);
            return r;
        }
        return ((delegate* unmanaged<IntPtr, V3>)fn)(obj);
    }

    /// <summary>A <c>CAurObject</c>'s world position through its own virtual -- the p2p target's, which is
    /// not necessarily a <c>Gob</c> and so is not read as a field. The slot is
    /// <see cref="EngineLayout.GobGetPositionSlot"/>, measured on both images.</summary>
    internal static V3 Position(IntPtr obj) => CallV3(new CX.Engine.Gob(obj).GetPositionFn, obj);
}
