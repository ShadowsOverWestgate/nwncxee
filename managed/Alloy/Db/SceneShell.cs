using System;
using System.Runtime.InteropServices;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>CX-120 (seam-scene): the words a <c>CAurScene</c> facade shell carries at the ENGINE's offsets,
/// kept current by the setters that change them. The game layer reads these without a vtable getter
/// (CX-182 §1a, §1d), so they are not a convenience -- a stale one is a
/// wrong answer in game code:
/// <list type="bullet">
/// <item><c>+0x130</c>/<c>+0x138</c>: the member list's array pointer and count. <c>GetAurSceneObjectCount</c>
/// @0x68b380 is <c>return *(u32*)(scene + 0x138)</c> and no vtable getter exists.</item>
/// </list>
/// <para><b>Two words the shell deliberately does NOT mirror</b>, because nothing in DB mode reads them:
/// <c>+0x168</c> (ambient) is written out of line by <c>CAurScene::SetAmbient</c> @0x561ab0 and read only by
/// <c>Scene::Render</c>'s <c>SetLightModel</c> -- engine renderer code that does not run -- and CX hooks that
/// setter anyway, so the column is the one truth. <c>+0x1b8</c> is the external-animate counter, and the story runs the other way round from the one
/// this file first told: <c>Camera::RenderScene</c> WRITES it (<c>Camera.c:3037</c>, on the off-screen skip)
/// and <c>Scene::Render</c> increments it (<c>Scene.c:5582</c>); the READER is <c>Gob::Animate</c> @0x59d8c0
/// (<c>Gob.c:871-872</c>), which latches it into <c>Gob+0x458</c> so an object animates once per scene pass
/// however many times it is reached. In DB mode both writers are CX's and the reader is seam-anim's, so the
/// counter is a DB concern and not a shell word; the shell only has to be big enough that
/// <c>CAurObject::AnimateExternal</c>'s own increment lands inside its block (seam-obj's sizing job).</para>
/// <para>The <c>+0x08</c>/<c>+0x10</c>/<c>+0x14</c> SafePointer back-reference list is the game's to mutate in
/// place (it inlines <c>CExoArrayList</c>, <c>realloc</c> and all); the shell gives it the three words and
/// seam-obj's retire path nulls the targets. Nothing here touches them.</para>
/// <para>The member mirror is a NATIVE array of object-shell pointers -- not DbIds -- because that is what the
/// game would find in an engine <c>Scene</c>'s list, and the one consumer that walks it rather than reading
/// the count would then be reading pointers it can call. It is rebuilt on a cold edge (join, leave, draw-order
/// move), never per frame.</para></summary>
public static unsafe class SceneShell
{
    /// <summary>Where the member list's array pointer and count sit. <c>EngineLayout</c>'s own rows, per OS
    /// and build-locked, because they are the ENGINE's class layout and the one direct reader
    /// (<c>GetAurSceneObjectCount</c> @0x68b380) is engine code compiled against it -- not a CX invention
    /// that happens to agree.</summary>
    public static int ObjListPtr => EngineLayout.SceneGobs;
    public static int ObjListCount => EngineLayout.SceneGobCount;

    private static ExtenderLog? _log;
    private static long _rebuilds, _mirrorBytes;

    internal static void Attach(ExtenderLog log) => _log = log;

    /// <summary>Per scene, the native array behind <see cref="ObjListPtr"/> and its capacity in entries.</summary>
    private static readonly nint[] _mirror = new nint[SceneDb.MaxScenes];
    private static readonly int[] _mirrorCap = new int[SceneDb.MaxScenes];

    /// <summary>Per scene, "the native array no longer matches the list".</summary>
    private static readonly bool[] _stale = new bool[SceneDb.MaxScenes];

    /// <summary>The scene's member list changed (a join, a leave, or a <c>RenderGob*</c> move). O(1): write
    /// the COUNT word, which is the only one anything reads, and mark the array stale.
    /// <para>Rebuilding the array here instead made an area load O(N^2) -- 1155 tiles each rebuilding a
    /// growing array. And it bought nothing: the one game-layer reader of
    /// <c>GetAurSceneObjectCount</c> (<c>CGuiPanel.c:511</c>) compares the count against 1 and never touches
    /// the array. So the array is rebuilt lazily, when someone actually asks (<see cref="Flush"/>), and the
    /// count -- the thing that IS read -- is always current.</para></summary>
    public static void CountChanged(DbId scene)
    {
        if (!SceneDb.Scenes.Valid(scene)) return;
        int i = scene.Index;
        _stale[i] = true;
        nint shell = SceneDb.SceneFacade[i];
        if (shell == 0) return;
        *(int*)(shell + ObjListCount) = SceneDb.SceneObjects[i]?.Count ?? 0;
    }

    /// <summary>Bring the native array at <c>Scene+0x130</c> up to date if anything is about to read it.
    /// Called from the <c>GetAurSceneObjectCount</c> hook, because the game's one array walk follows its
    /// count call immediately.</summary>
    public static void Flush(DbId scene)
    {
        if (!SceneDb.Scenes.Valid(scene)) return;
        int i = scene.Index;
        if (!_stale[i]) return;
        _stale[i] = false;
        DbList? m = SceneDb.SceneObjects[i];
        int n = m?.Count ?? 0;
        if (n > _mirrorCap[i])
        {
            int want = n < 16 ? 16 : n * 2;
            nint grown = (nint)NativeMemory.Realloc((void*)_mirror[i], (nuint)want * (nuint)sizeof(nint));
            if (grown == 0)
            {
                _log?.Always($"SceneShell: could not grow scene {i}'s member mirror to {want} entries -- "
                           + "the game's direct read of the scene object list will be SHORT");
                return;
            }
            _mirror[i] = grown;
            _mirrorBytes += (want - _mirrorCap[i]) * (long)sizeof(nint);
            _mirrorCap[i] = want;
        }
        nint* a = (nint*)_mirror[i];
        for (int k = 0; k < n; k++)
        {
            DbId o = m![k];
            a[k] = SceneDb.Objects.Valid(o) ? SceneDb.ObjFacade[o.Index] : 0;
        }
        _rebuilds++;
        nint shell = SceneDb.SceneFacade[i];
        if (shell == 0) return;
        *(nint*)(shell + ObjListPtr) = _mirror[i];
        *(int*)(shell + ObjListCount) = n;
    }

    /// <summary>The shell was just minted: write every mirrored word at once.</summary>
    public static void Refresh(DbId scene) { CountChanged(scene); Flush(scene); }

    /// <summary>The scene record is dying: hand the mirror back, after nulling the two shell words that point
    /// at it. The order matters -- the shell outlives this call by its quarantine, and a shell left naming a
    /// freed array would hand a late <c>GetAurSceneObjectCount</c> a dangling pointer instead of a zero count,
    /// which is the one failure the poison table cannot describe.</summary>
    public static void Release(DbId scene)
    {
        int i = scene.Index;
        if ((uint)i >= SceneDb.MaxScenes || _mirror[i] == 0) return;
        nint shell = SceneDb.SceneFacade[i];
        if (shell != 0)
        {
            *(nint*)(shell + ObjListPtr) = 0;
            *(int*)(shell + ObjListCount) = 0;
        }
        NativeMemory.Free((void*)_mirror[i]);
        _mirrorBytes -= _mirrorCap[i] * (long)sizeof(nint);
        _mirror[i] = 0;
        _mirrorCap[i] = 0;
    }

    internal static string Stats(int _) => $"scene shells: {_rebuilds} list rebuilds, {_mirrorBytes / 1024} KiB mirrors";
}
