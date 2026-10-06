using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>
/// CX-107: the engine side of the ported behaviour solvers -- the two by-value setters whose ABI differs
/// per platform, and the weak-reference bookkeeping a solve does around its owner.
/// </summary>
/// <remarks>
/// <para><c>CAurObject::SetPosition(Vector)</c> and <c>SetOrientation(Quaternion)</c> cannot be declared
/// through <c>[Fn(...)]</c>: the MSVC mangling
/// (<c>?SetPosition@Gob@@UEAA?AVVector@@V2@@Z</c>) says both take their argument by value AND return it by
/// value, and the two platforms answer that differently -- SysV in xmm0/xmm1 with no buffer anywhere, Win64
/// through a caller buffer as <c>(this, sret, &amp;arg)</c>. Same shape and same reason as
/// <see cref="AurXform"/>, which already carries the getters; the measurements are in
/// <c>CX-107</c> §6.2 (Linux @0x437190/@0x4371c0, Windows rva 0x95210/0x94f80).
/// The Windows deleting destructor is a third case: MSVC's single destructor slot takes a flags argument
/// (<c>mov edx,1</c> at rva 0x202897) that Itanium's does not.</para>
/// <para>The setters must go through the virtual: <c>CNWCVisualEffect</c>, <c>CNWCCreature</c>,
/// <c>CNWCItem</c>, <c>CNWCObject</c>, <c>CNWCAreaOfEffectObject</c>, <c>CNWCSoundObject</c> and
/// <c>StringGob</c> all override <c>SetPosition</c>, and a VFX gob is exactly what an attach behaviour
/// drives. The attach SNAP is the one write the engine does as a raw field store, and the port matches
/// that too.</para>
/// <para>Hot: one solve per behaviour per frame. Nothing here allocates on the managed heap; the one
/// allocation is the engine's own <c>CExoArrayList</c> growth inside <see cref="Register"/>, which is what
/// vanilla does on the same line.</para>
/// <para><b>Live-unverified, and the first thing to check if attached objects look wrong:</b> the SysV
/// argument halves of <see cref="SetPos"/> and <see cref="SetRot"/>. Passing a 12-byte three-float struct in
/// xmm0/xmm1 through a C# function pointer is the same trick <c>AurXform.Gust</c> already uses; passing a
/// 16-byte four-float one is new here (the repo only RETURNED a <c>Quat</c> that way before). A wrong
/// classification would not be subtle -- every attached VFX would take a garbage orientation on its first
/// frame, and <c>behav.owned=0</c> puts it back.</para>
/// </remarks>
internal static unsafe class BehaviorCall
{
    /// <summary>An engine <c>SafePointer&lt;CAurObject&gt;</c>: the object, and the index it occupies in
    /// that object's back-reference list. 0x10 bytes, which is what
    /// <c>CAurBehavior::Control</c>'s <c>operator delete(p, 0x10)</c> says.</summary>
    [StructLayout(LayoutKind.Sequential, Size = 0x10)]
    internal struct SafePtr
    {
        public IntPtr Ptr;
        public int Index;
    }

    private static int* _screenW;
    private static int* _screenH;

    /// <summary>Resolves every symbol the solvers reach: the five called through <c>EngineFn</c> and the two
    /// screen-size globals. Returns false after naming the first miss
    /// through <see cref="SymbolMap.Require"/>, and the caller then stands the whole feature down -- there is
    /// no half-solved state (no silent fallback).</summary>
    internal static bool Resolve(ExtenderLog log)
    {
        if (!SymbolMap.Require(log, "BehaviorSolvers", SymbolMap.SafePtrListAdd, SymbolMap.CameraSetViewPort,
                               SymbolMap.SceneGetObject, SymbolMap.GetGuiScale, SymbolMap.PartGetWorld,
                               SymbolMap.ScreenWidth, SymbolMap.ScreenHeight)) return false;
        _screenW = (int*)Native.Sym(SymbolMap.ScreenWidth);
        _screenH = (int*)Native.Sym(SymbolMap.ScreenHeight);
        return true;
    }

    // ---- the weak reference a solve holds on its owner ------------------------------------------

    /// <summary>Registers <paramref name="sp"/> in <paramref name="obj"/>'s back-reference list, the way
    /// every engine solve opens. If the object is destroyed before <see cref="Unregister"/>, its destructor
    /// nulls <c>sp.Ptr</c> -- which is the only way a solver can tell that the ground moved under it inside
    /// a dependency's <c>Animate</c>.</summary>
    internal static void Register(SafePtr* sp, IntPtr obj)
    {
        sp->Ptr = obj;
        sp->Index = -1;
        if (obj == IntPtr.Zero) return;
        // The ADDRESS of the CExoArrayList, which is what Add takes as its `this`; the count after it is a
        // field, and comes through the view.
        EngineFn.SafePtrListAdd(obj + EngineLayout.AurObjBackRefs, (IntPtr)sp);
        sp->Index = new CAurObject(obj).BackRefCount - 1;
    }

    /// <summary>The engine's inlined <c>SafePointer</c> teardown (<c>FUN_0059fe60</c>): swap the last entry
    /// into our slot, fix the moved entry's stored index, shrink. Order does not matter in a back-reference
    /// list, so this is a swap rather than the <c>memmove</c> <c>DelIndex</c> does.</summary>
    internal static void Unregister(SafePtr* sp)
    {
        IntPtr obj = sp->Ptr;
        if (obj == IntPtr.Zero) { sp->Index = -1; return; }
        var o = new CAurObject(obj);
        SafePtr** items = (SafePtr**)o.BackRefs;
        int i = sp->Index, n = o.BackRefCount;
        if (i >= 0 && i < n)
        {
            n--;
            items[i] = items[n];
            o.BackRefCount = n;
        }
        if (i < n) items[i]->Index = i;
        sp->Index = -1;
    }

    /// <summary>A behaviour's own dependency list: item <paramref name="i"/> and the count. A dead entry
    /// (its object gone, so its <c>Ptr</c> nulled) is SKIPPED rather than pruned: the engine prunes it with
    /// <c>DelIndex</c> and <c>operator delete</c>, and <c>operator delete</c> is libstdc++'s on Linux and
    /// un-exported inside <c>nwmain.exe</c> on Windows, so there is no portable way to free what the engine
    /// allocated. <c>CAurBehavior::~CAurBehavior</c> @0x49feb0 frees every remaining entry unconditionally,
    /// so leaving the tombstone in place leaks nothing -- it costs one null compare per solve until the
    /// behaviour dies.</summary>
    internal static SafePtr* Dep(IntPtr behavior, int i) => ((SafePtr**)new CAurBehavior(behavior).Deps)[i];

    /// <inheritdoc cref="Dep"/>
    internal static int DepCount(IntPtr behavior) => new CAurBehavior(behavior).DepCount;

    // ---- the object virtuals ---------------------------------------------------------------------

    /// <summary>The owner's position and orientation through their own virtuals
    /// (<see cref="AurXform"/> owns those two argument lists).</summary>
    internal static Vector3 GetPos(IntPtr obj)
    {
        AurXform.V3 v = AurXform.Position(obj);
        return new Vector3(v.X, v.Y, v.Z);
    }

    /// <inheritdoc cref="GetPos"/>
    internal static Quat GetRot(IntPtr obj) => AurXform.Orientation(obj);

    /// <summary><c>CAurObject::SetPosition(Vector)</c> through the vtable. SysV takes the vector in
    /// xmm0/xmm1 with <c>this</c> in rdi and returns it the same way; Win64 takes
    /// <c>(this, sret, &amp;vector)</c>. The returned copy is discarded on both.</summary>
    internal static void SetPos(IntPtr obj, in Vector3 p)
    {
        IntPtr fn = new Gob(obj).SetPositionFn;
        AurXform.V3 v = new() { X = p.X, Y = p.Y, Z = p.Z };
        if (OperatingSystem.IsWindows())
        {
            AurXform.V3 ret;
            ((delegate* unmanaged<IntPtr, AurXform.V3*, AurXform.V3*, void>)fn)(obj, &ret, &v);
            return;
        }
        ((delegate* unmanaged<AurXform.V3, IntPtr, AurXform.V3>)fn)(v, obj);
    }

    /// <summary><c>CAurObject::SetOrientation(Quaternion)</c> through the vtable -- the same two argument
    /// lists with a 16-byte payload (SysV: xmm0 = (w, x), xmm1 = (y, z)).</summary>
    internal static void SetRot(IntPtr obj, in Quat q)
    {
        IntPtr fn = new Gob(obj).SetOrientationFn;
        Quat v = q;
        if (OperatingSystem.IsWindows())
        {
            Quat ret;
            ((delegate* unmanaged<IntPtr, Quat*, Quat*, void>)fn)(obj, &ret, &v);
            return;
        }
        ((delegate* unmanaged<Quat, IntPtr, Quat>)fn)(v, obj);
    }

    /// <summary><c>CAurObject::Animate(dt)</c> through the vtable -- what a solve calls on each dependency
    /// before reading its part transforms. Virtual for real (<c>Camera</c>, <c>CNWCAnimBase</c> override
    /// it), so this is not <c>EngineFn.GobAnimate</c>.</summary>
    internal static void Animate(IntPtr obj, float dt) => new Gob(obj).Animate(dt);

    /// <inheritdoc cref="Animate"/>
    internal static IntPtr GetBehavior(IntPtr obj, int type) => new Gob(obj).GetBehavior(type);

    /// <summary><c>CAurObject::SetBehavior(b)</c>. Passing null DELETES the behaviour, so a caller must not
    /// touch <c>this</c> after it returns.</summary>
    internal static void SetBehavior(IntPtr obj, IntPtr b) => new Gob(obj).SetBehavior(b);

    /// <summary>The deleting destructor. Itanium's takes <c>this</c> alone; MSVC's single slot takes a flags
    /// word, and 1 is "free the storage" -- which is what the engine passes
    /// (<c>mov edx,1</c>, rva 0x202897).</summary>
    internal static void DeleteObject(IntPtr obj)
    {
        IntPtr fn = new Gob(obj).DeleteFn;
        if (OperatingSystem.IsWindows()) ((delegate* unmanaged<IntPtr, int, void>)fn)(obj, 1);
        else ((delegate* unmanaged<IntPtr, void>)fn)(obj);
    }

    /// <summary>The three lookups the subwindow solver makes: the panel gob's name (its inline buffer) and
    /// scene, and the owning camera behind the <c>CAurObject</c>.</summary>
    internal static IntPtr GetName(IntPtr obj) => new Gob(obj).GetName();
    /// <inheritdoc cref="GetName"/>
    internal static IntPtr GetScene(IntPtr obj) => new Gob(obj).GetScene();
    /// <inheritdoc cref="GetName"/>
    internal static IntPtr AsCamera(IntPtr obj) => new Gob(obj).AsCamera();

    /// <summary><c>Part::GetWorld(Vector&amp;, Quaternion&amp;)</c> -- two out-pointers, void return, so
    /// one declaration covers both platforms. The engine dispatches it virtually, but <c>Part</c>'s is the
    /// only implementation in either image (every Part-subclass vtable holds it), so the port calls the
    /// symbol <see cref="SymbolMap.PartGetWorld"/> already resolves.</summary>
    internal static void PartGetWorld(IntPtr part, Vector3* pos, Quat* rot) =>
        EngineFn.PartGetWorld(part, pos, (float*)rot);

    /// <summary><c>CAurora::GetGUIScale()</c> -- the live UI multiplier, floor 1.0.</summary>
    internal static float GuiScale() => EngineFn.GetGuiScale();

    /// <summary><c>Camera::SetViewPort(x, y, w, h)</c> and <c>Scene::GetObject(name)</c>: both virtual with
    /// one implementation in either image, so both are called by symbol.</summary>
    internal static void SetViewPort(IntPtr cam, float x, float y, float w, float h) =>
        EngineFn.CameraSetViewPort(cam, x, y, w, h);

    /// <inheritdoc cref="SetViewPort"/>
    internal static IntPtr SceneGetObject(IntPtr scene, byte* name) =>
        EngineFn.SceneGetObject(scene, name);

    /// <summary>The window the engine lays its GUI out in, for the subwindow solver's upscale rect.</summary>
    internal static float ScreenW => _screenW == null ? 0f : *_screenW;
    /// <inheritdoc cref="ScreenW"/>
    internal static float ScreenH => _screenH == null ? 0f : *_screenH;
}
