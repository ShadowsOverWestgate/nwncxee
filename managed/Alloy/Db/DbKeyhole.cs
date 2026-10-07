using System;
using System.Numerics;
using CX.Engine;

namespace CX.Alloy.Db;

/// <summary>CX-120: <b>the keyhole, fed from the scene database.</b> The hole vanilla cuts through a roof so
/// the player is visible indoors.
/// <para>Mode 0 asks the engine's camera per part (<c>Camera::GetCanKeyholeDissolve</c>, reproduced from a
/// cached box in <c>PartTable.KeyholeDissolvesBox</c>). DB mode reads a COLUMN instead --
/// <c>AlloyRenderer.Route</c>'s DB branch is <c>SceneDb.PartDissolve[part] &lt; 1f</c> -- and nothing ever
/// wrote that column. It is seeded to 1 (opaque) and stayed there, so no DB part ever dissolved: the inn's
/// roof stayed solid and the PC sat under it in the dark while mode 0 cut the hole.</para>
/// <para>This is the writer. Same rule, same camera words, over the database's own world boxes.</para></summary>
internal static unsafe class DbKeyhole
{
    /// <summary>Write <see cref="SceneDb.PartDissolve"/> for every staged DB part of this pass.
    /// <para>The camera words come off the DB world camera's SHELL. They are the engine's own: the chase
    /// behaviour CX attaches is the engine's <c>CAurBehaviorCameraFollow</c> running on a layout-compatible
    /// shell (ADR 0028 section 2), so it writes the keyhole byte, plane and cut exactly where the engine's
    /// camera carries them -- there is nothing to recompute, only somewhere to read.</para>
    /// <para>TILE parts only, which is the database's spelling of the engine's own refusal: its rule tests
    /// <c>part+0x50 == 0</c> (a part with no gob), and a gob part carries no world box to test anyway
    /// (<c>ComputePartBBox</c> runs from <c>Scene::SpawnTile</c> alone). A placeable inside the keyhole is not
    /// dissolved by vanilla either.</para></summary>
    internal static void Write(nint cameraShell, int[] order, int n, int[] slotPart, DbId camera)
    {
        Runtime(camera);
        byte on = 0;
        float cut = 0f;
        float p0 = 0f, p1 = 0f, p2 = 0f, p3 = 0f;
        if (cameraShell != 0)
        {
            var c = new Camera(cameraShell);
            on = c.KeyholeState;
            cut = c.KeyholeCut;
            System.Span<float> pl = c.KeyholePlane;
            p0 = pl[0]; p1 = pl[1]; p2 = pl[2]; p3 = pl[3];
        }
        if (on == 0) { _off++; }
        else _armed++;
        for (int k = 0; k < n; k++)
        {
            int i = order[k];
            int part = slotPart[i];
            if (part < 0) continue;
            SceneDb.PartDissolve[part] = on != 0 && Dissolves(part, i, on, p0, p1, p2, p3, cut) ? 0f : 1f;
        }
    }

    /// <summary>The engine's verdict for one database part, over the world box the stage already wrote
    /// (<c>_oBox</c>, filled when the slot is seated and refreshed on every move, so it is never unwritten).</summary>
    private static bool Dissolves(int part, int i, byte on, float p0, float p1, float p2, float p3, float cut)
    {
        DbId obj = SceneDb.PartObj[part];
        if (!SceneDb.Objects.Valid(obj) || (SceneDb.ObjFlags[obj.Index] & ObjFlag.Tile) == 0) return false;
        float* plane = stackalloc float[4] { p0, p1, p2, p3 };
        fixed (float* b = &AlloyRenderer._oBox[i * 6])
            if (!PartTable.KeyholeDissolvesBox(b, on, plane, cut)) return false;
        _cut++;
        return true;
    }

    /// <summary>The camera's own keyhole block, written the way <c>Camera::RenderScene</c> @0x5a7920 writes
    /// it -- the body CX's DB camera slot replaces, which is exactly why DB mode never had any of this. The
    /// aperture read SHUT in all 33911 passes of the previous run because nothing filled the byte.
    /// <para>The engine's rule, verbatim: both global shader params 8 and 9 on and the camera has a scene,
    /// else the state byte is 0. Then the target is the SCENE's anchor (<c>scene+0x190</c>, the point the sky
    /// is kept on and the one vanilla centres the hole on), the distance is target-to-camera, and:
    /// <c>cut = GetGlobalShaderParamf(0x17) + target.z</c>; the plane is the camera's first view-volume plane
    /// with its xyz NEGATED and <c>w = -w - (dist - near)</c> -- note <c>cam+0x4cc</c> is the NEAR plane, not
    /// the far one.</para>
    /// <para>Said once, with every input, because this reads six words off a CX shell and any one of them
    /// being unfed is a different bug with the same symptom.</para></summary>
    internal static void WriteCameraState(nint cameraShell, DbId camId, DbId scene)
    {
        if (cameraShell == 0) return;
        var cam = new Camera(cameraShell);
        nint sc = SceneDb.Scenes.Valid(scene) ? SceneDb.SceneFacade[scene.Index] : 0;
        // The WORLD camera only. This slot runs for every camera the frame draws through, GUI panels
        // included, and a panel's camera has no roof to pierce -- the previous round's one-shot reported a
        // GUI camera's numbers for exactly that reason.
        if (!SceneDb.Scenes.Valid(scene) || !SceneDb.SceneIsWorld[scene.Index]) return;
        int p8 = AlloyLights.GlobalShaderParami(8), p9 = AlloyLights.GlobalShaderParami(9);
        bool on = p8 != 0 && p9 != 0 && sc != 0;
        if (!on) { cam.KeyholeState = 0; SayOnce(p8, p9, sc, default, 0f, 0f); return; }
        // The target and the near plane come from the DATABASE, not off the shell. Measured: a CX camera
        // shell carries near = 0 and its scene carries skyPos = (0,0,0), because both are filled by engine
        // paths DB mode replaces -- so the engine's own expression would centre the hole on the origin at a
        // zero camera range. The database's answers are the same two quantities: the followed object is what
        // the chase camera is built over, and FrameInputs.CamNear is the near this frame was projected with.
        System.Numerics.Vector3 target = Target(camId, sc);
        // The rendered eye, for the same reason the aperture's terms take it: the shell's own position word is
        // not where the view was built (measured 17,46 from the PC at the PC's height, against an eye 14 units
        // above it), and a plane pushed back by the wrong distance cuts at the wrong depth.
        System.Numerics.Vector3 eye = DbCamera.PosOf(camId);
        float dist = (target - eye).Length();
        // The CAMERA RECORD's near, not FrameInputs': this slot runs per camera and FrameInputs carries
        // whichever view was last published, which measured 81,92 here against the record's 0,1 -- an 80-unit
        // shift of the aperture plane.
        float near = SceneDb.CamNear[camId.Index];
        cam.KeyholeState = 1;
        cam.KeyholeCut = AlloyLights.GlobalShaderParamf(0x17) + target.Z;
        // Plane 0 -- the NEAR plane -- from the DATABASE's own volume, not off the shell's +0x4e8 list, which
        // is empty in DB mode (measured 0x0 every pass) because the engine path that fills it is the one this
        // slot replaces. DbCamera.ViewVolume hands back exactly what Camera::GetViewVolume(int) would: the
        // engine's plane order (near, far, then the four sides) and the engine's inside-NEGATIVE sign, which
        // is what this expression was written against. GetViewVolume @0x5a3a60 is
        // `*(Vec4*)(items + i*16)`, so index 0 is the first 16 bytes the decompiled block reads.
        if (!SceneDb.CamVolumeSet[camId.Index])
        { cam.KeyholeState = 0; SayOnce(p8, p9, sc, target, dist, near, 0); return; }
        Plane pl = DbCamera.ViewVolume(camId, 0);
        System.Span<float> kh = cam.KeyholePlane;
        kh[0] = -pl.Normal.X; kh[1] = -pl.Normal.Y; kh[2] = -pl.Normal.Z;
        kh[3] = -pl.D - (dist - near);
        SayOnce(p8, p9, sc, target, dist, near, 1);
    }

    /// <summary>What the hole is centred on: the camera's followed object, falling back to the scene's sky
    /// anchor (which is what vanilla reads) when there is no object to ask.</summary>
    private static System.Numerics.Vector3 Target(DbId camId, nint sc)
    {
        if (SceneDb.Cameras.Valid(camId))
        {
            // CamFOLLOW, not CamObj. CamObj is the camera's OWN object -- its position is the camera's, which
            // is what this read, so uPlayerPos was the eye and the shader's floor clamp
            // (world.z - 1 - uPlayerPos.z) zeroed the level for every roof below the camera: the whole keyhole
            // cut, refused on one wrong column. Measured against mode 0: player (17,00 4,00 0,00) there,
            // (1,68 4,00 14,36) -- the eye exactly -- here.
            DbId obj = SceneDb.CamFollow[camId.Index];
            if (SceneDb.Objects.Valid(obj)) return SceneDb.ObjPos[obj.Index];
        }
        return sc == 0 ? default : new Scene(sc).SkyPos;
    }

    private static bool _said;
    private static int _calls;
    /// <summary>Calls before the state is reported. The first call of a session is at the main menu, before
    /// the engine has set either global shader param, so a one-shot there says "0 0" about nothing -- the
    /// same trap the frame diff's pass counter exists for.</summary>
    private const int SayAt = 600;

    private static void SayOnce(int p8, int p9, nint sc, System.Numerics.Vector3 target, float dist, float near, nint vv = 0)
    {
        if (_said || ++_calls < SayAt) return;
        _said = true;
        AlloyRenderer._log?.Always($"AlloyRenderer: [db] keyhole state -- param8={p8} param9={p9} scene=0x{sc:x} "
                                + $"target=({target.X:F2},{target.Y:F2},{target.Z:F2}) dist={dist:F2} near={near:F2} "
                                + $"viewVolume=0x{vv:x}");
    }

    /// <summary>The keyhole's SHAPE, which is the shader's half of it. The CPU verdict is only an enable bit
    /// -- <c>CxKeyhole</c> cuts the aperture from <c>uPlayer</c>, <c>uKeyholeCam</c> (the player-to-camera
    /// distance and the camera pitch) and <c>uKeyholeDia</c> -- and those come off the engine's
    /// <c>ShaderExposedRuntimeData</c> block, which the ENGINE's render path fills. That path does not run in
    /// DB mode, so the block held zeros: the aperture maths ran on a zero player at zero distance and the
    /// "hole" was the whole tile. That is why switching the enable on removed roofs instead of piercing them.
    /// <para>The block is renderer state, not scene memory, so filling it here is the same kind of write as
    /// any other uniform -- and it keeps one code path in the uniform upload for both modes.</para></summary>
    private static void Runtime(DbId camera)
    {
        if (AlloyRenderer.Rt.Ptr == 0 || !SceneDb.Cameras.Valid(camera)) return;
        ShaderExposedRuntimeData rt = AlloyRenderer.Rt;
        int ci = camera.Index;
        // CamFOLLOW, not CamObj -- the same column <see cref="Target"/> reads, and the same trap. CamObj is the
        // CAurObject the camera itself IS ((CAurObject*)(camera + 8)), so its position is the EYE: uPlayerPos
        // was the camera, uKeyholeCam.x (eye-to-"player") was 0 and the pitch fell to its 90-degree fallback,
        // so CxKeyhole's first line read `off = depth - 0 + 0` -- positive for every fragment in front of the
        // camera, which returns 1.0 and cuts nothing. 28 parts a frame carried the enable bit and the keyhole
        // program ran for 0,58 ms of GPU without punching one texel.
        DbId obj = SceneDb.CamFollow[ci];
        // The RENDERED eye -- DbCamera.PosOf, the camera sub-object's position, which is the point BuildView
        // puts the view matrix at. The raw CamPos column is where NewCAurCamera left it and the game's own
        // writes land in the sub-object instead: measured (x,y,0) at the player's own height, 17,46 from the
        // PC, where the view this frame was built at (1,68 4,00 14,36). Read that way round the pitch came out
        // 0 degrees for a camera 14 units up.
        System.Numerics.Vector3 eye = DbCamera.PosOf(camera);
        // The followed object IS the player for the world camera: the chase behaviour is constructed over it
        // (CameraSlots.SetFollow), which is the same object vanilla's keyhole centres on.
        System.Numerics.Vector3 player = SceneDb.Objects.Valid(obj) ? SceneDb.ObjPos[obj.Index] : eye;
        rt.PlayerPos = player;
        rt.CamPos = eye;
        rt.CamFocus = player;
        System.Numerics.Vector3 d = eye - player;
        float dist = d.Length();
        rt.CamDist = dist;
        // Degrees above the horizon, POSITIVE as the camera rises -- what uKeyholeCam.y is measured in
        // (cx_keyhole.glsl divides (90 - pitch) by 45, so 90 is straight down and 0 is level). Measured
        // against mode 0, which reads 50 on this shot; the first cut of this read -73,19, the right magnitude
        // with the wrong sign and origin, which shifts `off` by nearly three.
        float flat = MathF.Sqrt(d.X * d.X + d.Y * d.Y);
        rt.CamPitch = dist > 1e-4f ? MathF.Abs(MathF.Atan2(d.Z, flat)) * (180f / MathF.PI) : 90f;
        _rtWrites++;
    }

    private static long _armed, _off, _cut, _rtWrites;

    internal static string Stats() => $"dbkeyhole: {_armed} passes armed / {_off} with the aperture shut, {_cut} part dissolves, {_rtWrites} runtime fills";
}
