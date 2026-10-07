using System;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Framework;

namespace CX.Alloy.Seam;

/// <summary>CX-120 (seam-scene): the <c>Scene</c>-only slots -- the 37 the concrete class carries beyond the
/// <c>CAurScene</c> interface (Linux indices 51..87) -- and which of them CX fills.
///
/// <para><b>Five are filled.</b> Each one has a measured caller outside the engine's renderer module:</para>
/// <list type="table">
/// <item><term>64 <c>AddObject(CAurObject*)</c></term><description>what <c>Gob::AttachToScene</c> @0x460c80
/// calls to put an object in a scene -- the same edge seam-obj's <c>AttachToScene</c> slot takes.</description></item>
/// <item><term>65 <c>ChildAdd(Gob*)</c> / 66 <c>ChildRemove(Gob*)</c></term><description>read off
/// <c>GobRenderLast</c> @0x53f000, whose whole body is <c>scene = gob+0x88</c> then vtbl <c>+0x210</c> then
/// vtbl <c>+0x208</c> -- remove, then re-append last. SEVEN game callers (<c>CGuiDragSource</c> twice,
/// <c>CPanelAreaMap</c> five times), so these two are the only Scene-only slots the game layer provably
/// reaches.</description></item>
/// <item><term>81 <c>IsAnimating()</c></term><description>the scene's own answer, folded over its members'
/// animation state.</description></item>
/// <item><term>86 <c>GetShadowGob()</c></term><description>the pair of interface slot 29
/// <c>SetShadowGob</c>; one line, and a getter whose setter exists is not worth poisoning.</description></item>
/// </list>
///
/// <para><b>The other 32 are POISONED BY DESIGN</b>, which is a decision and not a gap.
/// <c>RenderSinglePass</c>, <c>RenderOnlyGobs</c>, <c>RenderShadows</c>, <c>RenderStaticShadows</c>,
/// <c>RenderBeams</c>, <c>RenderDynamicGeometry</c>, <c>RenderLensFlares</c>, <c>RenderLines</c>,
/// <c>DoBinBucket</c>, <c>DoEmitterBucket</c>, <c>DoTriggerBucket</c>, <c>DoHighlightGob</c>,
/// <c>DoHighlightTriggers</c>, <c>DoGobShadows</c>, <c>DoDynamicMeshProjections</c>, <c>DoFog</c>,
/// <c>PostProcessLS</c>, <c>InitBins</c>, <c>HasBins</c>, <c>AddFacesToBins</c>,
/// <c>ListSpecialParts(R)</c>, <c>ListSpringPartsR</c>, <c>UnlistSpecialParts</c>,
/// <c>SetSingleTileFlame</c>, <c>SetWindUpdateTimeout</c>, <c>RecomputeStaticProjections</c>,
/// <c>ReloadGrass</c>, <c>ResetTileFading</c>, <c>FadeDoors</c>, <c>GrassIsEnabled</c> and
/// <c>GrassFarRenderDistance</c> are the engine renderer's own entry points, reached from inside that module
/// and nowhere else (CX-182 §3: the callgraph has ZERO calls from any
/// caller at or above 0x600000 into <c>Part::*</c>, and the only Scene calls from the game layer are the three
/// <c>CAurScene::SetAmbient</c> ones). In DB mode that module does not run, so a call to one of these is a
/// CX bug or a mod the seam has not accounted for -- and the poison table is what says so by name in
/// <c>cx.log</c> instead of jumping through a NULL word.
/// <para>Two of them do have CX work behind the same NAME, reached through the interface instead:
/// <c>ResetTileFading</c> (slot 75) is the interface's <c>ResetAreaTileFading</c> (slot 16), and
/// <c>ReloadGrass</c> (74) is <c>ReloadAreaGrass</c> (15). Both pairs exist because the engine's concrete
/// class has an inner and an outer form; CX answers the outer one and poisons the inner, which is also how a
/// caller reaching the inner one gets named.</para></summary>
internal static unsafe class SceneOnlySlots
{
    /// <summary>Linux slot indices of the <c>Scene</c>-only block.</summary>
    internal const int First = 51, Last = 87;

    /// <summary>Slot index -> entry point for the five CX fills; every other index in the block is 0, which
    /// seam-obj's vtable builder fills with the poison entry for that slot NAME.</summary>
    internal static readonly IntPtr[] Table = Build();

    private static long _childAdds, _childRemoves, _addObjects;

    private static IntPtr[] Build()
    {
        var t = new IntPtr[Last + 1 - First];
        t[64 - First] = (IntPtr)(delegate* unmanaged<nint, nint, void>)&AddObject;
        t[65 - First] = (IntPtr)(delegate* unmanaged<nint, nint, void>)&ChildAdd;
        t[66 - First] = (IntPtr)(delegate* unmanaged<nint, nint, void>)&ChildRemove;
        t[81 - First] = (IntPtr)(delegate* unmanaged<nint, byte>)&IsAnimating;
        t[86 - First] = (IntPtr)(delegate* unmanaged<nint, nint>)&GetShadowGob;
        return t;
    }

    [UnmanagedCallersOnly]
    private static void AddObject(nint self, nint obj)
    {
        _addObjects++;
        DbScene.Join(Facade.SceneId(self), Facade.ObjectId(obj));
    }

    /// <summary>Slot 65. <c>GobRenderLast</c> calls <see cref="ChildRemove"/> then this, so an add on an
    /// object already in the scene must APPEND (which is what <see cref="DbScene.Join"/> does) -- that
    /// remove/add pair IS the engine's "draw this last".</summary>
    [UnmanagedCallersOnly]
    private static void ChildAdd(nint self, nint obj)
    {
        _childAdds++;
        DbScene.Join(Facade.SceneId(self), Facade.ObjectId(obj));
    }

    [UnmanagedCallersOnly]
    private static void ChildRemove(nint self, nint obj)
    {
        _childRemoves++;
        DbScene.Leave(Facade.SceneId(self), Facade.ObjectId(obj));
    }

    [UnmanagedCallersOnly]
    private static byte IsAnimating(nint self) => (byte)(DbScene.IsAnimating(Facade.SceneId(self)) ? 1 : 0);

    [UnmanagedCallersOnly]
    private static nint GetShadowGob(nint self)
    {
        DbId s = Facade.SceneId(self);
        if (!SceneDb.Scenes.Valid(s)) return 0;
        DbId o = SceneDb.SceneShadowGob[s.Index];
        return SceneDb.Objects.Valid(o) ? SceneDb.ObjFacade[o.Index] : 0;
    }

    internal static string Stats(int _)
        => $"scene-only: {_addObjects} AddObject, {_childAdds} ChildAdd, {_childRemoves} ChildRemove";
}
