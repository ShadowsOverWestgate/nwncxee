using System;
using System.Numerics;
using CX.Engine;
using CX.Framework;
using CX.Services.Camera;

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
    /// <para>TILE and STATIC parts, which is the database's spelling of the engine's own refusal: its rule
    /// tests <c>part+0x50 == 0</c> (a part with no gob), which is what <c>Scene::SpawnTile</c> makes -- and a
    /// STATIC placeable is spawned through that same call (the <c>Quaternion*</c> path, <c>TileBuild.PlaceStatic</c>),
    /// so vanilla's hole takes non-interactable placeables with the roof. Tile-only here left them standing
    /// solid in the hole. An interactable placeable is a gob and stays.</para></summary>
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
        Write(on, cut, p0, p1, p2, p3, order, n, slotPart, camera);
    }

    /// <summary>The column itself, over camera words already read. Split from the shell read so the
    /// self-check can exercise both arms -- the shell is engine memory and there is none off-device.</summary>
    private static void Write(byte on, float cut, float p0, float p1, float p2, float p3,
                             int[] order, int n, int[] slotPart, DbId camera)
    {
        // With the aperture shut the verdict is 1f for every part whatever the plane and the cut say, so the
        // words are zeroed out of the key: an indoor camera that shut the hole must not keep re-writing the
        // column because its stale plane drifts.
        if (on == 0) { _off++; cut = 0f; p0 = 0f; p1 = 0f; p2 = 0f; p3 = 0f; }
        else _armed++;
        // CX-188: O(changed), not O(14k ordered records) a frame. Every input of the loop below is in this key:
        //   - the camera's six keyhole words, and which camera they came off;
        //   - the slot SET (AlloyRenderer.DbSetGen: a seat or a release, which is what moves _oDbPart and the
        //     order itself) and the order's length;
        //   - the world boxes the verdict tests (AlloyRenderer.BoxGen -- a box that MOVED, not a re-send);
        //   - the LOD swaps (DbAnim.LodGen). Both alternates stay live across a swap, so it marks Dirty.Tree
        //     and nothing else: the ordered set can change identity with DbSetGen and n both standing still.
        // The column is seeded to 1f and reset to 1f when a part record is freed (DbFeed.Records), so a part
        // this skip never reaches is opaque, which is what the walk would have written.
        if (_lastCam == camera.Value && _lastOn == on && _lastCut == cut
            && _lastP0 == p0 && _lastP1 == p1 && _lastP2 == p2 && _lastP3 == p3
            && _lastSet == AlloyRenderer.DbSetGen && _lastBox == AlloyRenderer.BoxGen
            && _lastLod == DbAnim.LodGen && _lastN == n)
        { _skipped++; return; }
        _lastCam = camera.Value; _lastOn = on; _lastCut = cut;
        _lastP0 = p0; _lastP1 = p1; _lastP2 = p2; _lastP3 = p3;
        _lastSet = AlloyRenderer.DbSetGen; _lastBox = AlloyRenderer.BoxGen;
        _lastLod = DbAnim.LodGen; _lastN = n;
        for (int k = 0; k < n; k++)
        {
            int i = order[k];
            int part = slotPart[i];
            if (part < 0) continue;
            SceneDb.PartDissolve[part] = on != 0 && Dissolves(part, i, on, p0, p1, p2, p3, cut) ? 0f : 1f;
        }
        _sweeps++;
    }

    /// <inheritdoc cref="Write(byte, float, float, float, float, float, int[], int, int[], DbId)"/>
    private static uint _lastCam;
    private static byte _lastOn;
    private static float _lastCut, _lastP0, _lastP1, _lastP2, _lastP3;
    private static int _lastSet = -1, _lastBox = -1, _lastLod = -1, _lastN = -1;

    /// <summary>The engine's verdict for one database part, over the world box the stage already wrote
    /// (<c>_oBox</c>, filled when the slot is seated and refreshed on every move, so it is never unwritten).</summary>
    private static bool Dissolves(int part, int i, byte on, float p0, float p1, float p2, float p3, float cut)
    {
        DbId obj = SceneDb.PartObj[part];
        if (!SceneDb.Objects.Valid(obj) || (SceneDb.ObjFlags[obj.Index] & (ObjFlag.Tile | ObjFlag.Static)) == 0) return false;
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
        if (!SceneDb.Cameras.Valid(camId) || !SceneDb.Objects.Valid(SceneDb.CamFollow[camId.Index]) || Directed())
        { cam.KeyholeState = 0; _directed++; return; }
        // The target and the near plane come from the DATABASE, not off the shell. Measured: a CX camera
        // shell carries near = 0 and its scene carries skyPos = (0,0,0), because both are filled by engine
        // paths DB mode replaces -- so the engine's own expression would centre the hole on the origin at a
        // zero camera range. The database's answers are the same two quantities: the followed object is what
        // the chase camera is built over, and FrameInputs.CamNear is the near this frame was projected with.
        System.Numerics.Vector3 target = SceneDb.ObjPos[SceneDb.CamFollow[camId.Index].Index];
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

    /// <summary>Is the camera someone else's than the player's -- the free camera, or the engine's cutscene flag
    /// (<c>CClientExoApp::InCutscene</c>, the int <c>SetCutsceneMode</c> @0x745580 stores)? A directed shot shows
    /// the scene whole: no roof is cut.</summary>
    private static bool Directed()
    {
        if (FreeCameraService.Active) return true;
        IntPtr app = CameraService.App();
        return app != IntPtr.Zero && new CClientExoApp(app).Internal.CutsceneMode != 0;
    }

    private static long _directed;

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

    private static long _armed, _off, _cut, _rtWrites, _skipped, _sweeps;

    internal static string Stats() => $"dbkeyhole: {_armed} passes armed / {_off} with the aperture shut ({_directed} for a directed or unfollowing camera), {_cut} part dissolves, {_rtWrites} runtime fills, {_sweeps} column sweep(s) / {_skipped} pass(es) skipped (nothing the verdict reads moved)";

    /// <summary>Off-device: the SKIP, which is the only thing here that can be wrong without looking wrong --
    /// a stale dissolve column is a roof that stays open (or shut) after the camera moved.
    /// <para>BOTH arms. The shut arm writes 1f over the order; the ARMED arm is reached with a non-tile
    /// subject, which <see cref="Dissolves"/> refuses before the engine call -- so the camera words go through
    /// the key and the verdict still needs no engine memory. Both directions in each arm: the skip must skip
    /// when nothing it reads moved, and must NOT skip when the slot set, a world box, a LOD swap or any
    /// camera word did.</para></summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "DbKeyhole.SelfCheck");
        // Save and restore the live key: this runs inside a session whose next pass must not inherit it.
        var was = (_lastCam, _lastOn, _lastCut, _lastP0, _lastP1, _lastP2, _lastP3,
                   _lastSet, _lastBox, _lastLod, _lastN);

        DbId obj = SceneDb.Objects.Alloc();
        if (obj.IsNull) { log.Info("DbKeyhole.SelfCheck: FAILED -- no object slot"); return false; }
        if (!SceneDb.Parts.AllocRange(1, out int part))
        { SceneDb.Objects.Free(obj); log.Info("DbKeyhole.SelfCheck: FAILED -- no part slot"); return false; }
        SceneDb.PartObj[part] = obj;
        // NOT a tile or a static: Dissolves refuses such a part before it reaches PartTable.KeyholeDissolvesBox, which
        // is engine code there is none of off-device. That refusal is what makes the armed arm checkable.
        SceneDb.ObjFlags[obj.Index] &= ~(ObjFlag.Tile | ObjFlag.Static);
        var order = new[] { 0 };
        var slotPart = new[] { part };

        // ---- the aperture SHUT ----
        SceneDb.PartDissolve[part] = 0f;
        _lastSet = -1; _lastBox = -1; _lastLod = -1; _lastN = -1; _lastCam = 0;
        Write(0, 0f, 0f, 0f, 0f, 0f, order, 1, slotPart, default);
        check.Expect(SceneDb.PartDissolve[part] == 1f, "a shut aperture writes every ordered part opaque");

        // Nothing the verdict reads has moved, so the second pass must not touch the column at all.
        SceneDb.PartDissolve[part] = 0.25f;
        Write(0, 0f, 0f, 0f, 0f, 0f, order, 1, slotPart, default);
        check.Expect(SceneDb.PartDissolve[part] == 0.25f,
                     "a pass with nothing changed re-wrote the dissolve column -- the skip is not skipping");
        // ...and a camera word moving while the hole is SHUT must still not re-write it: the verdict does not
        // read the plane or the cut in that arm, and a stale plane drifts every frame.
        Write(0, 7f, 1f, 2f, 3f, 4f, order, 1, slotPart, default);
        check.Expect(SceneDb.PartDissolve[part] == 0.25f,
                     "a shut aperture's drifting plane re-wrote the column -- the words are not zeroed out of the key");

        // ---- each edge in the key breaks the skip ----
        AlloyRenderer.DbSetGen++;
        Write(0, 0f, 0f, 0f, 0f, 0f, order, 1, slotPart, default);
        check.Expect(SceneDb.PartDissolve[part] == 1f, "a slot-set edge did not re-write the column");
        SceneDb.PartDissolve[part] = 0.25f;
        AlloyRenderer.BoxGen++;
        Write(0, 0f, 0f, 0f, 0f, 0f, order, 1, slotPart, default);
        check.Expect(SceneDb.PartDissolve[part] == 1f, "a world-box change did not re-write the column");
        SceneDb.PartDissolve[part] = 0.25f;
        DbAnim.LodGen++;
        Write(0, 0f, 0f, 0f, 0f, 0f, order, 1, slotPart, default);
        check.Expect(SceneDb.PartDissolve[part] == 1f,
                     "a LOD swap did not re-write the column -- a swap changes which seated slot the order "
                   + "carries and moves neither DbSetGen nor the order's length");

        // ---- the aperture ARMED ----
        SceneDb.PartDissolve[part] = 0.25f;
        Write(1, 3f, 0f, 0f, 1f, -2f, order, 1, slotPart, default);
        check.Expect(SceneDb.PartDissolve[part] == 1f,
                     "arming the aperture did not re-write the column (the ON byte is not in the key)");
        SceneDb.PartDissolve[part] = 0.25f;
        Write(1, 3f, 0f, 0f, 1f, -2f, order, 1, slotPart, default);
        check.Expect(SceneDb.PartDissolve[part] == 0.25f, "an armed pass with the same camera words re-walked");
        Write(1, 4f, 0f, 0f, 1f, -2f, order, 1, slotPart, default);
        check.Expect(SceneDb.PartDissolve[part] == 1f, "the keyhole CUT is not in the key -- the hole would "
                                                     + "cut at the previous frame's depth");
        SceneDb.PartDissolve[part] = 0.25f;
        Write(1, 4f, 0f, 0f, 1f, -2.5f, order, 1, slotPart, default);
        check.Expect(SceneDb.PartDissolve[part] == 1f, "the keyhole PLANE is not in the key -- the hole would "
                                                     + "stay where the camera last was");

        SceneDb.Parts.FreeRange(part, 1);
        SceneDb.Objects.Free(obj);
        SceneDb.Parts.TakeDirty(); SceneDb.Objects.TakeDirty();
        (_lastCam, _lastOn, _lastCut, _lastP0, _lastP1, _lastP2, _lastP3,
         _lastSet, _lastBox, _lastLod, _lastN) = was;
        log.Info(check.Ok ? "DbKeyhole.SelfCheck: passed"
                          : "DbKeyhole.SelfCheck: FAILED -- the dissolve column's skip would hide a real change");
        return check.Ok;
    }
}
