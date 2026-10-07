using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy.Seam;

/// <summary>CX-120 (seam-scene): the scene- and camera-shaped <c>Aur*</c> free functions, and the decision
/// for each one -- hooked, or answered by the shell and left alone. CX-182
/// §1a/§1d/§2 is the input; what follows is the resolution, because a function that needs no hook is a fact
/// worth stating once rather than a hook worth installing.
///
/// <list type="table">
/// <item><term><c>NewCAurScene</c></term><description>HOOKED. It is a factory for an engine object; CX answers
/// with a record + facade.</description></item>
/// <item><term><c>NewCAurCamera</c></term><description>HOOKED. Same, plus the <c>CAurObject</c> sub-object at
/// +8.</description></item>
/// <item><term><c>CAurScene::SetAmbient(Vector)</c></term><description>HOOKED. An out-of-line interface member
/// the game links NON-virtually, writing <c>Scene+0x168</c>/<c>+0x170</c> by field. There is no slot for it,
/// so a facade cannot answer it.</description></item>
/// <item><term><c>GetAurSceneObjectCount</c></term><description>NOT hooked. Its whole body is
/// <c>return *(u32*)(scene + 0x138)</c> @0x68b380, and <see cref="SceneShell"/> keeps that word equal to the
/// member list on every join, leave and draw-order move. Hooking it as WELL would be a second answer to one
/// question; the mirror is the answer, because it is also what a caller that inlines the read sees.</description></item>
/// <item><term><c>GuiAttachViewport</c></term><description>NOT hooked. Read in full (@0x68bda0): two
/// <c>Gob::GetPart(char const*)</c> vcalls, <c>operator new(0x48)</c>, the engine's own
/// <c>CAurBehaviorSubwindow</c> constructor over <c>(CAurObject*)(camera + 8)</c>, a byte at
/// <c>behavior+0x40</c>, then <c>Camera::SetBehavior</c> through the camera's vtable. The ONE thing that is
/// not a vcall is the <c>+8</c> literal, and a layout-compatible camera shell satisfies it exactly -- so the
/// engine's own body works on a CX camera and the behaviour object stays the game's, which is what ADR 0028
/// section 2 wants. (It takes FIVE arguments, not three: <c>(CAurCamera*, CAurObject*, char const*,
/// char const*, bool)</c> -- the direct-access note's three-argument form was from the demangled display
/// name, and the mangled symbol <c>_Z17GuiAttachViewportP10CAurCameraP10CAurObjectPKcS4_b</c> is the
/// authority.)</description></item>
/// <item><term><c>GetGameobjectHit</c></term><description>NOT hooked. Read in full (@0x75dfd0): two
/// <c>CHitInfo</c> field reads -- kind at <c>+0x24</c>, the hit <c>CAurObject*</c> at <c>+0x48</c> -- then
/// <c>GetExternalDataType</c> (CAurObject slot 33) and <c>GetExternalData</c> (slot 31) through the object's
/// vtable, and the <c>+0x48</c> upcast only for an ITEM (type 6). Both slots are CX's, so the engine's body
/// answers correctly the moment the hit test fills a <c>CHitInfo</c> in that layout.
/// <see cref="HitTest.HitKind"/>/<see cref="HitTest.HitObject"/> is that contract, and it is the hit stream's
/// to honour.</description></item>
/// <item><term><c>GuiMeasureHeightDifference</c>, <c>GuiMeasureLength</c>,
/// <c>StopSceneChangeHack</c></term><description>NOT hooked: pure vcalls, already noted safe.</description></item>
/// </list></summary>
internal static unsafe class SceneFns
{
    private static ExtenderLog? _log;
    private static long _newScene, _newCamera, _ambientWrites, _findHits, _findMisses;

    /// <summary>Install the three hooks. False = one did not take, which is a seam that cannot be armed: the
    /// caller stands DB mode down rather than letting the engine's own factory build an object nothing owns.</summary>
    internal static bool Install(ExtenderLog log, HookService hooks)
    {
        _log = log;
        bool ok = true;
        ok &= Hook(log, hooks, SymbolMap.NewCAurScene, (IntPtr)(delegate* unmanaged<byte*, nint>)&OnNewCAurScene);
        ok &= Hook(log, hooks, SymbolMap.NewCAurCamera, (IntPtr)(delegate* unmanaged<byte*, nint>)&OnNewCAurCamera);
        ok &= Hook(log, hooks, SymbolMap.CAurSceneSetAmbient, (IntPtr)(delegate* unmanaged<nint, VectorArg, void>)&OnSetAmbient);
        ok &= Hook(log, hooks, SymbolMap.FindScene, (IntPtr)(delegate* unmanaged<byte*, nint>)&OnFindScene);
        ok &= Hook(log, hooks, SymbolMap.AuroraSetTileFoggedByWar, (IntPtr)(delegate* unmanaged<Vec3*, byte, void>)&OnSetTileFoggedByWar);
        // The area's height step, chained off the tileset resolve: without it EVERY tile spawn refuses, so a
        // failure here is a failure of the whole tile build and is reported as one.
        ok &= TileSetSeam.Install(log, hooks);
        ok &= Hook(log, hooks, SymbolMap.SetAlphaObject, (IntPtr)(delegate* unmanaged<nint, float, void>)&OnSetAlpha);
        return ok;
    }

    private static bool Hook(ExtenderLog log, HookService hooks, string symbol, IntPtr handler)
    {
        if (hooks.Install(symbol, handler) != IntPtr.Zero) return true;
        log.Always($"SceneFns: {symbol} did not hook -- the CX scene database cannot answer for it, so DB mode "
                 + "stands down (the engine's own body would build an engine object instead)");
        return false;
    }

    /// <summary><c>NewCAurScene(char const*)</c>: a CX scene record and its facade. The original is NEVER
    /// called -- that is the whole point of the switch, and it is why a failed hook is a stand-down rather
    /// than a fallback.</summary>
    [UnmanagedCallersOnly]
    private static nint OnNewCAurScene(byte* name)
    {
        string n = Marshal.PtrToStringUTF8((IntPtr)name) ?? "";
        DbId id = DbScene.Create(n);
        if (id.IsNull) return 0;
        nint shell = Facade.Mint(FacadeKind.Scene, id);
        if (shell == 0) { DbScene.Destroy(id); return 0; }
        SceneDb.SceneFacade[id.Index] = shell;
        SeedFog(shell);
        SeedWind(shell);
        SceneShell.Refresh(id);
        _newScene++;
        return shell;
    }

    /// <summary><c>FindScene(char const*)</c>: answer the engine's registry lookup from the CX scene table.
    /// <para>The engine's own array is EMPTY in DB mode -- its factory, which is what appends to it, is the
    /// body this seam replaces -- so the original returns null and every game-layer caller that reaches a
    /// scene by NAME quietly does nothing. The area's fog is the visible one: <c>CNWCArea::SetFog</c> and
    /// <c>SetFogAmount</c> drive it as <c>FuncInterp("mainscene.fog*")</c> TEXT, which resolves through here,
    /// not through a <c>CAurScene</c> vtable slot. <c>AuroraSetTileFoggedByWar</c> reaches its scene the same
    /// way (and would dereference null without this, which is why CX hooks it too).</para>
    /// <para>Case-insensitive, like the <c>strcasecmp</c> it replaces. Null for no match IS the engine's
    /// contract for "no such scene" -- the callers test it.</para></summary>
    [UnmanagedCallersOnly]
    private static nint OnFindScene(byte* name)
    {
        if (name == null) return 0;
        string n = Marshal.PtrToStringUTF8((IntPtr)name) ?? "";
        for (int i = 0; i < SceneDb.MaxScenes; i++)
        {
            if (!SceneDb.Scenes.LiveAt(i)) continue;
            if (!string.Equals(SceneDb.SceneName[i], n, StringComparison.OrdinalIgnoreCase)) continue;
            _findHits++;
            return SceneDb.SceneFacade[i];
        }
        _findMisses++;
        return 0;
    }

    /// <summary>Give the shell a real <c>WindManager</c> at <c>+0x160</c>, the way <c>Scene::Scene</c> does.
    /// <para>This is not decoration. The game sets an area's wind as console TEXT -- <c>CNWCArea::SetWind</c>
    /// emits <c>setglobalwind %f %f %f</c> and <c>setglobalwindvariation %f %f</c> through <c>FuncInterp</c>
    /// -- and both commands resolve the scene with <c>FindScene</c> and then dereference
    /// <c>scene+0x160</c> with no null check (<c>WindManager::SetGlobalWind</c>,
    /// <c>SetMaximumDeviation</c>). While <c>FindScene</c> returned null they printed "Couldn't find scene";
    /// the moment CX answered it, a zero there was a SIGSEGV on area load.</para>
    /// <para>The engine's own ctor runs over the block rather than a transcription of it: it fills two
    /// 0x400-byte noise fields and stores pointers INTO ITSELF at <c>+0x838</c>/<c>+0x840</c>, and it leaves
    /// the emitter count at <c>+0x868</c> zero, which is what keeps <c>SetGlobalWind</c>'s
    /// <c>CalculateRadius</c> loop from walking an emitter list CX does not have.</para>
    /// <para>The game then writes base wind, max yaw and max pitch into CX memory, and the DB consumers
    /// (<c>EmitterSim</c>, <c>DanglyPhysics</c>) read the same block through the ordinary
    /// <c>CX.Engine.WindManager</c> view. Released with the shell.</para></summary>
    private static void SeedWind(nint shell)
    {
        var sc = new Scene(shell);
        if (sc.IsNull) return;
        WindManagerCtorFn ctor = EngineFn.WindManagerCtor;
        int size = EngineLayout.WindManagerSize;
        if (ctor == null || size <= 0) return;
        nint block = (nint)NativeMemory.AllocZeroed((nuint)size);
        ctor(block);
        sc.WindManager = block;
        _windBlocks++;
    }

    /// <summary>Free the scene's <c>WindManager</c> block. Called from the scene's destroy, so the block's
    /// lifetime is the shell's.</summary>
    internal static void ReleaseWind(nint shell)
    {
        var sc = new Scene(shell);
        if (sc.IsNull) return;
        nint block = sc.WindManager;
        if (block == 0) return;
        sc.WindManager = 0;
        NativeMemory.Free((void*)block);
        _windBlocks--;
    }

    private static long _windBlocks;

    /// <summary>The fog words <c>Scene::Scene</c> @0x4724f0 leaves behind, written into a fresh shell.
    /// <para>A CX shell is zero-filled, and zero is not the engine's "no fog yet": the ctor stores
    /// <c>0x40a0000000000000</c> at <c>+0x140</c> and <c>0x3f80000042200000</c> at <c>+0x148</c>, i.e. fog
    /// off, near 5, far 40, white. The game only ever overwrites SOME of those (the area sets colour and
    /// enable through <c>FuncInterp</c>), so a shell that started at zero would fog from 0 to 0 the moment
    /// anything enabled it.</para></summary>
    private static void SeedFog(nint shell)
    {
        var sc = new Scene(shell);
        if (sc.IsNull) return;
        sc.FogEnable = 0;
        sc.FogStart = CtorFogNear;
        sc.FogEnd = CtorFogFar;
        sc.FogColor = Vector3.One;
    }

    /// <summary><c>Scene::Scene</c>'s own fog near, the high half of its <c>+0x140</c> store.</summary>
    private const float CtorFogNear = 5f;
    /// <summary><c>Scene::Scene</c>'s own fog far, the low half of its <c>+0x148</c> store.</summary>
    private const float CtorFogFar = 40f;

    /// <summary><c>NewCAurCamera(char const*)</c>. A camera is TWO records: the camera row and the
    /// <c>CAurObject</c> row its <c>+8</c> sub-object resolves to -- <c>GuiAttachViewport</c> and
    /// <c>Camera::AsCAurObject</c> both reach it, and <c>CAurBehaviorSubwindow</c> stores a SafePointer into
    /// its <c>+0x08</c> list. seam-obj mints the object row with the shell
    /// (<see cref="FacadeParts.NewCameraObject"/>).</summary>
    [UnmanagedCallersOnly]
    private static nint OnNewCAurCamera(byte* name)
    {
        string n = Marshal.PtrToStringUTF8((IntPtr)name) ?? "";
        DbId obj = FacadeParts.NewCameraObject(n);
        DbId id = DbCamera.Create(obj);
        if (id.IsNull) return 0;
        nint shell = Facade.Mint(FacadeKind.Camera, id);
        if (shell == 0) { DbCamera.Destroy(id); return 0; }
        SceneDb.CamFacade[id.Index] = shell;
        // The object row's shell is the camera block's +8 INTERIOR -- the literal pointer the engine passes
        // as the camera's CAurObject* (GuiAttachViewport @0x68bda0 writes (CAurObject*)(camera + 8)), and the
        // address Facade.GobOf already maps back to this row. Without the forward half, every reader of
        // ObjFacade saw a camera as shell-less: BehaviorSeam.Attached then wrote no attach word, so the
        // engine's own CAurBehaviorSubwindow::Control failed its "am I still the owner's behaviour" test and
        // returned before Camera::SetViewPort -- every GUI viewport camera kept an empty rect.
        if (SceneDb.Objects.Valid(obj)) SceneDb.ObjFacade[obj.Index] = shell + SeamLayout.CameraGobSub;
        _newCamera++;
        return shell;
    }

    /// <summary><c>CAurScene::SetAmbient(Vector)</c>: the column is the truth, and the shell does NOT mirror
    /// <c>+0x168</c> -- the only reader of that word is <c>Scene::Render</c>'s <c>SetLightModel</c>, engine
    /// renderer code that does not run in DB mode (<see cref="SceneShell"/>'s remarks).</summary>
    [UnmanagedCallersOnly]
    private static void OnSetAmbient(nint scene, VectorArg colour)
    {
        DbId s = Facade.SceneId(scene);
        if (!SceneDb.Scenes.Valid(s)) return;
        Vec3 c = colour.Value;
        SceneDb.SceneAmbient[s.Index] = new Vector3(c.X, c.Y, c.Z);
        SceneFeed.LightsChanged(s);
        _ambientWrites++;
    }

    /// <summary><c>AuroraSetTileFoggedByWar(Vector&amp;, bool)</c> @0x453320: the fog of war, and the only tile
    /// call that is a free function rather than a scene slot -- it works on the engine's current scene, so CX
    /// answers it for the WORLD scene, which is the only scene a tile grid can belong to.</summary>
    [UnmanagedCallersOnly]
    private static void OnSetTileFoggedByWar(Vec3* pos, byte fogged)
    {
        if (pos == null) return;
        DbId world = WorldScene();
        if (world.IsNull) return;
        TileBuild.SetFoggedByWar(world, new Vector3(pos->X, pos->Y, pos->Z), fogged != 0);
        _fogOfWar++;
    }

    /// <summary>The session's one world scene, cached. <c>AuroraSetTileFoggedByWar</c> is called per TILE as
    /// the fog of war moves, so the linear scan this used to do was 256 slots per cell. The cache is
    /// invalidated by the generation check alone: a scene that dies takes its handle with it.</summary>
    private static DbId WorldScene()
    {
        if (SceneDb.Scenes.Valid(_world) && SceneDb.SceneIsWorld[_world.Index]) return _world;
        _world = default;
        for (int i = 1; i < SceneDb.Scenes.High; i++)
            if (SceneDb.Scenes.LiveAt(i) && SceneDb.SceneIsWorld[i]) { _world = SceneDb.Scenes.IdAt(i); break; }
        return _world;
    }

    private static DbId _world;

    private static long _fogOfWar;

    /// <summary><c>setalpha(CAurObject*, float)</c> @0x4524d0 -- a thin glue function the game links
    /// non-virtually whose body reads <c>Gob+0x90</c> (the root <c>Part*</c>) and RECURSES the part tree,
    /// writing each part's alpha. There is no vtable slot for it, so a facade cannot answer it and the engine
    /// body would walk CX memory as an engine <c>Part</c>: measured, that is a segfault at the first module
    /// load (<c>CPanelScreenFade::SetFade</c>, which every area transition runs).
    /// <para>The port is faithful to the recursion: the engine writes EVERY part of the tree, not the object,
    /// so this writes the object's whole part range. <c>ponytail:</c> the alpha belongs to the object side,
    /// and seam-obj should reclaim this hook along with <c>Gob+0x90</c>; it is here because it is what
    /// unblocked the ladder.</para></summary>
    [UnmanagedCallersOnly]
    private static void OnSetAlpha(nint obj, float alpha)
    {
        DbId o = Facade.ObjectId(obj);
        if (!SceneDb.Objects.Valid(o)) return;
        int first = SceneDb.ObjPartFirst[o.Index], n = SceneDb.ObjPartCount[o.Index];
        for (int i = 0; i < n; i++)
        {
            SceneDb.PartAlpha[first + i] = alpha;
            SceneDb.TouchPart(first + i, Dirty.Material);
        }
        _alphaWrites++;
    }

    private static long _alphaWrites;

    internal static string Stats(int _)
        => $"scene fns: {_newScene} scenes, {_newCamera} cameras, {_ambientWrites} ambient writes, "
         + $"{_fogOfWar} fog-of-war cells, {_alphaWrites} setalpha walks, "
         + $"FindScene {_findHits} found / {_findMisses} MISSED";
}
