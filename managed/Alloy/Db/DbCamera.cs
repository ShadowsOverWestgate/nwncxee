using System;
using System.Numerics;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>CX-120 (seam-scene): what a <c>CAurCamera</c> IS when CX owns the scene database -- a row in
/// <see cref="SceneDb.Cameras"/>, and the view/projection arithmetic over its columns.
/// <para>In tier 3 <see cref="AlloyCamera"/> MEASURES the engine's camera object: it hooks
/// <c>Camera::RenderScene</c> and reads <c>Camera+0x4c4..+0x4e0</c>. In DB mode the camera IS a record, so the
/// same arithmetic runs over columns and the engine's object does not exist. The arithmetic itself is NOT
/// re-derived here -- <see cref="AlloyCamera.Perspective"/>, <see cref="AlloyCamera.Ortho"/>,
/// <see cref="AlloyCamera.ViewOf"/>, <see cref="AlloyCamera.ViewportScales"/> and
/// <see cref="AlloyCamera.DepthRange"/> are the one copy, read off the engine term for term in
/// <c>CX-109</c> §1, and this class supplies their inputs from the record.</para>
/// <para>Everything here runs on the GAME thread: the game reads <c>GetPosition</c>, <c>MapToScreen</c>,
/// <c>MouseDir</c> and the hit tests synchronously and must not wait on the renderer (ADR 0028 section 5).</para></summary>
public static unsafe class DbCamera
{
    /// <summary>The ortho extent <c>Camera::RenderScene</c> derives: <c>cam+0x4c8 = viewportHeight * 0.01</c>,
    /// and the box is then <c>(0, aspect*h, 0, h)</c>.</summary>
    private const float OrthoUnitsPerPixel = 0.01f;
    /// <summary>Degrees to radians, halved -- the engine's own constant, <c>fovDeg * 0.017453292f * 0.5f</c>.</summary>
    private const float HalfFovToRad = MathF.PI / 360f;
    /// <summary><c>Camera::UpscaleViewport</c>'s first test: a game UI scale this close to 1 is no scale at all
    /// (@0x5a44b0), and the same epsilon gates <c>MapToScreen</c>'s and <c>MouseDir</c>'s rect division.</summary>
    private const float UpscaleEpsilon = 0.09f;

    private static ExtenderLog? _log;
    private static long _created, _destroyed, _refused, _views;

    internal static void Attach(ExtenderLog log) => _log = log;

    /// <summary>Create a camera record with the engine's own defaults: no viewport of its own (which is what
    /// makes it the world camera -- <c>Camera::SetViewPort</c> clamps a non-positive extent to 1, so a camera
    /// that ever set one cannot read as the world camera again), identity orientation, a null view volume.</summary>
    public static DbId Create(DbId obj)
    {
        DbId id = SceneDb.Cameras.Alloc();
        if (id.IsNull)
        {
            if (_refused++ == 0)
                _log?.Always($"DbCamera: the camera table is full ({SceneDb.MaxCameras} cameras) -- "
                           + "NewCAurCamera REFUSED; the game layer will get a null camera");
            return default;
        }
        int i = id.Index;
        SceneDb.CamScene[i] = default;
        SceneDb.CamPos[i] = Vector3.Zero;
        SceneDb.CamPrevPos[i] = Vector3.Zero;
        SceneDb.CamRot[i] = Quaternion.Identity;
        SceneDb.CamViewX[i] = 0f; SceneDb.CamViewY[i] = 0f; SceneDb.CamViewW[i] = 0f; SceneDb.CamViewH[i] = 0f;
        SceneDb.CamNear[i] = DefaultNear; SceneDb.CamFar[i] = DefaultFar;
        SceneDb.CamViewAngle[i] = DefaultViewAngle;
        SceneDb.CamFollow[i] = default; SceneDb.CamAttachTo[i] = default; SceneDb.CamAttachPart[i] = -1;
        SceneDb.CamBehavior[i] = 0;
        SceneDb.CamVolumeSet[i] = false;
        for (int k = 0; k < SceneDb.ViewPlanes; k++) SceneDb.CamVolume[i * SceneDb.ViewPlanes + k] = default;
        SceneDb.CamFadeA[i] = default; SceneDb.CamFadeB[i] = default;
        SceneDb.CamObj[i] = obj;
        SceneDb.CamFacade[i] = 0;
        _created++;
        return id;
    }

    /// <summary>The clip range a fresh camera carries, as <c>Camera::Camera</c> @0x5a6060 initialises
    /// <c>+0x4cc</c>/<c>+0x4d0</c>. A camera the game never calls <c>SetClipDist</c> on renders with these.</summary>
    /// <summary>The clip distances a camera has before anything sets them -- the ENGINE's own constructor
    /// defaults, read off the same store as the view angle: <c>Camera::Camera</c> @0x5a42c0 writes
    /// <c>0x42c800003dcccccd</c> across <c>+0x4cc</c>, whose low word <c>0x3dcccccd</c> is 0.1 (near) and whose
    /// high word <c>0x42c80000</c> is 100.0 (far).
    /// <para>The far plane was 45, which is not a distance at all -- it is the view ANGLE, and the two sat on
    /// adjacent lines. An area measures 340 units across and the camera stands ~14 above the floor, so a
    /// 45-unit far plane clipped away all but the camera's own feet: 303 draws submitted, a frustum that
    /// reached almost nothing, and a flat frame. Nothing in the game calls <c>SetClipDist</c> on the world
    /// camera -- the slot trace shows <c>GetClipDist</c> arriving and <c>SetClipDist</c> never -- because in
    /// vanilla the constructor has already supplied these.</para></summary>
    public const float DefaultNear = 0.1f, DefaultFar = 100f;

    /// <summary>Destroy the camera record. Its <c>CAurObject</c> sub-object record is seam-obj's to free, from
    /// the same destructor slot.</summary>
    public static void Destroy(DbId cam)
    {
        if (!SceneDb.Cameras.Valid(cam)) return;
        // A camera is TWO records -- the camera row and the CAurObject row its +8 sub-object resolves to
        // (NewCAurCamera mints both) -- and this freed only one, so every camera the game destroyed leaked an
        // object slot for the session. The GUI builds and tears down a camera per panel view, so that is a
        // steady drip, not a one-off.
        // Through the object destroy path, not a bare Free: the row can be in a scene's member list (a
        // subwindow behaviour attaches it) and carries a facade the game may still hold.
        DbId obj = SceneDb.CamObj[cam.Index];
        if (SceneDb.Objects.Valid(obj))
        {
            if (ObjectBridge.ObjectDestroy is { } destroy) destroy(obj);
            else { DbScene.ObjectDied(obj); SceneDb.Objects.Free(obj); }
        }
        SceneDb.CamObj[cam.Index] = default;
        SceneDb.CamFacade[cam.Index] = 0;
        SceneDb.CamScene[cam.Index] = default;
        SceneDb.CamBehavior[cam.Index] = 0;
        SceneDb.Cameras.Free(cam);
        _destroyed++;
    }

    /// <summary>Attach the camera to a scene (<c>Camera::AttachToScene</c>, which the engine routes through
    /// <c>Gob::SetScene</c>). Zero detaches.</summary>
    public static void AttachToScene(DbId cam, DbId scene)
    {
        if (!SceneDb.Cameras.Valid(cam)) return;
        SceneDb.CamScene[cam.Index] = scene;
        SceneDb.TouchCam(cam, Dirty.Tree);
    }

    /// <summary>Set the camera's position, keeping the previous one (<c>GetPreviousPosition</c>, which the
    /// engine answers off its own saved field and the motion-vector pass reads).</summary>
    public static void SetPosition(DbId cam, Vector3 p)
    {
        if (!SceneDb.Cameras.Valid(cam)) return;
        int i = cam.Index;
        SceneDb.CamPos[i] = p;
        // ...and the sub-object record with it, because that is what every reader takes as the authority.
        DbId pobj = SceneDb.CamObj[i];
        if (SceneDb.Objects.Valid(pobj))
        {
            SceneDb.ObjPos[pobj.Index] = p;
            // ...and the facade's own Camera+0xb0 words: a game-side behaviour's Control (the server list's
            // CAurBehaviorSubwindow) is followed by a pose read BACK from the shell (DbAnim.SyncFromShell), and
            // stale words there put the list camera at the origin, every row behind its near plane.
            nint sh = SceneDb.ObjFacade[pobj.Index];
            if (sh != 0) { float* w = (float*)(sh + Seam.SeamLayout.GobPos); w[0] = p.X; w[1] = p.Y; w[2] = p.Z; }
            SceneDb.Touch(pobj, Dirty.Xform);
        }
        SceneDb.CamVolumeSet[i] = false;
        SceneDb.TouchCam(cam, Dirty.Xform);
    }

    /// <summary>Latch every camera's previous position, once per frame. The engine keeps it at
    /// <c>Camera+0xcc</c> and writes it per FRAME, not per <c>SetPosition</c> -- so a camera the game moves
    /// three times in one frame still reports where it was last frame, which is what a motion vector means.
    /// Saving it inside the setter (as this first did) made "previous" mean "one setter ago".</summary>
    internal static void NewFrame()
    {
        for (int i = 1; i < SceneDb.Cameras.High; i++)
            if (SceneDb.Cameras.LiveAt(i)) { SceneDb.CamPrevPos[i] = PosAt(i); PublishShellAt(i); }
    }

    /// <summary>Republish one camera's place NOW, for a writer whose answer the engine-view readers
    /// (nameplates, the aim assist's projector, every <c>Camera::GetPosition</c>) must see in the frame it was
    /// written -- the camera-feel layer's pose rewrite. The per-frame walk alone leaves them a frame behind,
    /// which in mode 0 they never are.</summary>
    internal static void Publish(DbId cam)
    {
        if (SceneDb.Cameras.Valid(cam)) PublishShellAt(cam.Index);
    }

    /// <summary>Copy the camera's place into its facade SHELL, at the Camera-relative offsets the engine keeps
    /// it in: <c>+0xb0</c>/<c>+0xbc</c>, which ARE the gob half's <c>+0xa8</c>/<c>+0xb4</c> at <c>+8</c>. In the
    /// engine a <c>Camera</c> IS-A <c>Gob</c> and there is one storage; the database has three homes for it
    /// (the column, the sub-object record, the shell) and the GAME -- plus every CX feature that reads the live
    /// camera through an engine view -- takes the shell's word.
    /// <para>Nothing wrote it, so <c>Camera::GetPosition</c> answered the world ORIGIN and
    /// <c>GetOrientation</c> the identity all session. The overhead nameplates measure their distance from
    /// exactly that, so every plate's "distance" was its object's world-coordinate magnitude -- in an ordinary
    /// area 30-45 m, which sits permanently inside the fade band, and the plates rendered as if almost at the
    /// visible-distance limit. The identity rotation also pointed the behind-camera rejection down world -Z.</para>
    /// <para>Per FRAME rather than in the two setters, because the game moves the world camera through its
    /// CAurObject sub-object and never calls <c>Camera::SetPosition</c> at all; the frame walk covers both
    /// paths and is the same both-stores rule <see cref="WriteShellViewAngle"/> follows.</para></summary>
    private static unsafe void PublishShellAt(int i)
    {
        nint shell = SceneDb.CamFacade[i];
        if (shell == 0) return;
        if (EngineLayout.CamPos >= 0)
        {
            Vector3 p = PosAt(i);
            float* f = (float*)(shell + EngineLayout.CamPos);
            f[0] = p.X; f[1] = p.Y; f[2] = p.Z;
        }
        if (EngineLayout.CamRot < 0) return;
        DbId obj = SceneDb.CamObj[i];
        Quaternion q = SceneDb.Objects.Valid(obj) ? SceneDb.ObjRot[obj.Index] : SceneDb.CamRot[i];
        // W-FIRST in memory, which is how Gob keeps a quaternion and how Facade.SetXform writes one.
        float* r = (float*)(shell + EngineLayout.CamRot);
        r[0] = q.W; r[1] = q.X; r[2] = q.Y; r[3] = q.Z;
    }

    public static void SetOrientation(DbId cam, Quaternion q)
    {
        if (!SceneDb.Cameras.Valid(cam)) return;
        SceneDb.CamRot[cam.Index] = q;
        DbId robj = SceneDb.CamObj[cam.Index];
        if (SceneDb.Objects.Valid(robj))
        {
            SceneDb.ObjRot[robj.Index] = q;
            nint sh = SceneDb.ObjFacade[robj.Index];
            if (sh != 0) { float* w = (float*)(sh + Seam.SeamLayout.GobRot); w[0] = q.W; w[1] = q.X; w[2] = q.Y; w[3] = q.Z; }
            SceneDb.Touch(robj, Dirty.Xform);
        }
        SceneDb.CamVolumeSet[cam.Index] = false;
        SceneDb.TouchCam(cam, Dirty.Xform);
    }

    /// <summary><c>Camera::SetViewPort</c> @0x5a38a0 exactly: a non-positive extent is clamped to 1, which is
    /// the reason the world camera's 0x0 rect is a reliable discriminator.</summary>
    public static void SetViewPort(DbId cam, float x, float y, float w, float h)
    {
        if (!SceneDb.Cameras.Valid(cam)) return;
        int i = cam.Index;
        SceneDb.CamViewX[i] = x;
        SceneDb.CamViewY[i] = y;
        SceneDb.CamViewW[i] = w <= 0f ? 1f : w;
        SceneDb.CamViewH[i] = h <= 0f ? 1f : h;
        SceneDb.CamVolumeSet[i] = false;
        SceneDb.TouchCam(cam, Dirty.Xform);
    }

    public static void SetClipDist(DbId cam, float near, float far)
    {
        if (!SceneDb.Cameras.Valid(cam)) return;
        SceneDb.CamNear[cam.Index] = near;
        SceneDb.CamFar[cam.Index] = far;
        SceneDb.CamVolumeSet[cam.Index] = false;
        SceneDb.TouchCam(cam, Dirty.Xform);
    }

    /// <summary>The view angle a camera has before anything sets one -- the ENGINE's own constructor default,
    /// not zero. <c>Camera::Camera</c> @0x5a42c0 stores <c>0x3f80000042340000</c> across <c>+0x4c4</c>, whose
    /// low word is <c>0x42340000</c> = 45.0 (the high word, 1.0, is the field after it).
    /// <para>This is what the first DB-mode area actually died of, and it is worth spelling out because the
    /// symptom was five steps away from the cause: a camera at view angle 0 takes <c>BuildView</c>'s ORTHO
    /// branch, an ortho projection fails <c>AlloyLights.TryPerspective</c> (its w row is 0, not -1), a frame
    /// with no perspective never builds the clustered light rig, and every lit part in the world is then
    /// skipped as "unlit-pass" -- 1850 of them, an empty world and no error anywhere. Nothing in the game ever
    /// calls <c>SetViewAngle</c> on the world camera, because in vanilla it does not have to.</para></summary>
    public const float DefaultViewAngle = 45f;

    public static void SetViewAngle(DbId cam, float degrees)
    {
        if (!SceneDb.Cameras.Valid(cam)) return;
        SceneDb.CamViewAngle[cam.Index] = degrees;
        WriteShellViewAngle(cam, degrees);
        SceneDb.CamVolumeSet[cam.Index] = false;
        SceneDb.TouchCam(cam, Dirty.Xform);
    }

    // ---- the view ------------------------------------------------------------------------------------

    /// <summary>The rect this camera renders into: its own when it has one, else the whole draw viewport
    /// (<c>Camera::RenderScene</c> takes the latter from <c>RenderInterface::GetViewPort</c>). Divided by the
    /// game's UI scale when the scale is not 1 and the camera has a rect of its own -- the arithmetic
    /// <c>Camera::MapToScreen</c> @0x5a3d60 spells out, which is the same rect <c>UpscaleViewport</c> rewrites
    /// for the view. CX does not reproduce the engine's ~20-name <c>sceneGUI_*</c> centring table: in DB mode
    /// CX owns the panel layout, so the scale is applied to every panel camera uniformly.</summary>
    public static void Rect(DbId cam, float vpX, float vpY, float vpW, float vpH, float guiScale,
                            out float x, out float y, out float w, out float h)
    {
        x = vpX; y = vpY; w = vpW; h = vpH;
        if (!SceneDb.Cameras.Valid(cam)) return;
        int i = cam.Index;
        float cw = SceneDb.CamViewW[i], ch = SceneDb.CamViewH[i];
        // The engine's own test is "EITHER extent is zero" (Camera::MapToScreen @0x5a3d60), not "both".
        if (cw != 0f || ch != 0f) { x = SceneDb.CamViewX[i]; y = SceneDb.CamViewY[i]; w = cw; h = ch; }
        // ...and the UI-scale shrink is applied AFTER that choice, to whichever rect was taken -- the engine
        // puts it past the GetViewPort fallback's label, so a camera with no rect of its own is scaled too.
        if (MathF.Abs(guiScale - 1f) < UpscaleEpsilon) return;
        x += (w - w / guiScale) * 0.5f;
        y += (h - h / guiScale) * 0.5f;
        w /= guiScale;
        h /= guiScale;
    }

    /// <summary>CX-120: where this camera actually is.
    /// <para>In the engine a <c>Camera</c> IS-A <c>Gob</c>: there is ONE position, and the game moves the
    /// camera through the ordinary object setters. The database splits the two -- a camera record and the
    /// <c>CAurObject</c> sub-object record the shell carries at +8 -- so a game that moves the camera through
    /// its sub-object writes <c>ObjPos</c> while <c>CamPos</c> stays where <c>NewCAurCamera</c> left it.
    /// It does exactly that, and the result was a view matrix built at the world ORIGIN: 296 draws kept by the
    /// cull and not one fragment shaded, because the whole area sits in front of a camera looking the other
    /// way from (0,0,0).</para>
    /// <para>So the sub-object record is the authority whenever the camera has one, and
    /// <see cref="SetPosition"/> writes it. One storage, as the engine has one.</para></summary>
    internal static Vector3 PosOf(DbId cam) => PosAt(cam.Index);

    /// <inheritdoc cref="PosOf"/>
    /// <param name="i">The camera's record index, for the per-frame walk that has no handle.</param>
    internal static Vector3 PosAt(int i)
    {
        DbId obj = SceneDb.CamObj[i];
        return SceneDb.Objects.Valid(obj) ? SceneDb.ObjPos[obj.Index] : SceneDb.CamPos[i];
    }

    /// <inheritdoc cref="PosOf"/>
    internal static Quaternion RotOf(DbId cam)
    {
        DbId obj = SceneDb.CamObj[cam.Index];
        return SceneDb.Objects.Valid(obj) ? SceneDb.ObjRot[obj.Index] : SceneDb.CamRot[cam.Index];
    }

    /// <summary>CX-120: this camera's view angle, taking the SHELL's own word as the authority.
    /// <para>The game layer does not only call <c>SetViewAngle</c>: <c>Camera+0x4c4</c> is one of the fields it
    /// reaches by literal offset (<c>EngineLayout.CamViewAngle</c> exists for exactly that reason), and the
    /// facade is a layout-compatible shell, so such a write lands in the shell and never in the column. That is
    /// what left the world camera at a view angle of 0 -- which <c>BuildView</c> reads as the ORTHO branch, so
    /// the world was drawn through an orthographic projection, <c>TryPerspective</c> refused it, the lighting
    /// rig never built and every lit part in the area was skipped as "unlit-pass". An empty world.</para>
    /// <para>The slot setter writes both, so whichever path the game takes, the two agree.</para></summary>
    internal static unsafe float ViewAngleOf(DbId cam)
    {
        int i = cam.Index;
        nint shell = SceneDb.CamFacade[i];
        if (shell != 0 && EngineLayout.CamViewAngle >= 0)
        {
            float shellAngle = *(float*)(shell + EngineLayout.CamViewAngle);
            // A sane view angle only: the shell is game-written memory, and a NaN or an out-of-range value
            // must not become a projection. Anything else falls back to the column.
            if (shellAngle > 0f && shellAngle < 180f) return shellAngle;
        }
        return SceneDb.CamViewAngle[i];
    }

    private static unsafe void WriteShellViewAngle(DbId cam, float degrees)
    {
        nint shell = SceneDb.CamFacade[cam.Index];
        if (shell != 0 && EngineLayout.CamViewAngle >= 0) *(float*)(shell + EngineLayout.CamViewAngle) = degrees;
    }

    /// <summary>This camera's whole view state -- what <c>Camera::RenderScene</c> derives before it renders:
    /// viewport, aspect, projection (perspective or the ortho branch a view angle of 0 takes), view matrix and
    /// depth range. Pure arithmetic over columns; no GL, no engine call.</summary>
    internal static bool BuildView(DbId cam, float vpX, float vpY, float vpW, float vpH,
                                 int screenW, int screenH, float guiScale, out AlloyView v)
    {
        v = default;
        if (!SceneDb.Cameras.Valid(cam)) return false;
        int i = cam.Index;
        Rect(cam, vpX, vpY, vpW, vpH, guiScale, out float rx, out float ry, out float rw, out float rh);
        if (rw <= 0f || rh <= 0f) return false;
        // The engine skips a view that lies wholly off the screen (Camera.c:3031-3037: it ends the scene,
        // bumps the external-animate counter and returns without rendering). A panel dragged off the edge is
        // the case, and drawing it would cost a full view for nothing.
        if (screenW > 0 && screenH > 0
            && (rx >= screenW || ry >= screenH || rx + rw <= 0f || ry + rh <= 0f)) return false;
        v.X = (int)rx; v.Y = (int)ry; v.W = (int)rw; v.H = (int)rh;
        v.Aspect = rw / rh;
        v.Near = SceneDb.CamNear[i];
        v.Far = SceneDb.CamFar[i];
        v.Fovy = ViewAngleOf(cam);
        v.Perspective = v.Fovy > 0f;
        float unit = rh * OrthoUnitsPerPixel;
        v.Proj = v.Perspective ? AlloyCamera.Perspective(v.Fovy, v.Aspect, v.Near, v.Far)
                               : AlloyCamera.Ortho(0f, v.Aspect * unit, 0f, unit, v.Near, v.Far);
        AlloyCamera.ViewportScales(v.X, v.Y, v.W, v.H, screenW, screenH, out float xs, out float ys);
        v.View = AlloyCamera.ViewOf(PosOf(cam), RotOf(cam), xs, ys);
        ClipToScreen(ref v, screenW, screenH);
        DepthRange(SceneDb.CamScene[i], v.Fovy, out v.DepthNear, out v.DepthFar);
        v.Valid = true;
        _views++;
        return true;
    }

    /// <summary><c>GLRender::SetViewPortLight</c> @0x5cb4b0: a rect past the screen's right or top edge is cut
    /// back to it, and the view's x/y scale (already in <c>v.View</c>) squeezes the projection into the cut
    /// rect. The character sheet's resize viewport is (0, y, 8192, h) -- drawn uncut, it was scaled twice.</summary>
    internal static void ClipToScreen(ref AlloyView v, int screenW, int screenH)
    {
        if (screenW <= 0 || screenH <= 0) return;
        if (v.Y + v.H > screenH && screenH - v.Y > 0) v.H = screenH - v.Y;
        if (v.X + v.W > screenW && screenW - v.X > 0) v.W = screenW - v.X;
    }

    /// <summary><c>Camera::RenderScene</c>'s depth decision over a DB scene's NAME, the same test
    /// <see cref="AlloyCamera.DepthRange"/> makes over the engine's: a <c>sceneGUI</c> scene -- or any camera
    /// whose view angle is exactly 0 -- shares one depth buffer split by range, with five named overlays in
    /// front of the rest.</summary>
    public static void DepthRange(DbId scene, float fovy, out float near, out float far)
    {
        string name = SceneDb.Scenes.Valid(scene) ? SceneDb.SceneName[scene.Index] : "";
        bool gui = fovy == 0f || name.StartsWith("sceneGUI", StringComparison.Ordinal);
        if (!gui) { near = 0f; far = 1f; return; }
        bool front = name.StartsWith("sceneGUI_MouseOverlay", StringComparison.Ordinal)
                  || name.StartsWith("sceneGUI_Overlay", StringComparison.Ordinal)
                  || name.StartsWith("sceneGUI_pnl_vidopts", StringComparison.Ordinal)
                  || name.StartsWith("sceneGUI_PNL_CONNECTSTAT", StringComparison.Ordinal)
                  || name.StartsWith("sceneGUI_PNL_UTIL_OK", StringComparison.Ordinal);
        near = front ? GuiFrontNear : GuiBackNear;
        far = front ? GuiFrontFar : GuiBackFar;
    }

    /// <summary>The depth slices <c>Camera::RenderScene</c> @0x4a82e9 hands a view.</summary>
    public const float GuiFrontNear = 0f, GuiFrontFar = 0.02f, GuiBackNear = 0.021f, GuiBackFar = 0.1f;

    /// <summary>Fill the camera's world-space view volume from the view just built and latch
    /// <c>ViewVolumeSet</c>. <see cref="SceneDb.ViewPlanes"/> planes in the engine's own order -- near, far,
    /// then the four sides -- extracted from <c>view * proj</c>, which is the frustum
    /// <c>Camera::RenderScene</c> builds from its view-space corner rays and then rotates into world space.
    /// <para><b>Plane convention: <c>dot(n, p) + d &gt; 0</c> is OUTSIDE.</b> Measured, not assumed:
    /// <c>Camera::HitCheckViewVolumeNearest</c> @0x5a4060 rejects a candidate with
    /// <c>n.x*P.x + n.y*P.y + n.z*P.z + n.w &gt; 0.0</c>, and <c>Camera::RenderScene</c> builds plane 0 as
    /// <c>(0, 0, 1, near)</c> -- which, in a view space that looks down -Z, is negative in front of the
    /// camera. That is the OPPOSITE of <see cref="CullPass.Planes"/>' inside-positive form, so the extraction
    /// is reused and every plane is negated here. <c>GetViewVolume</c> hands the game the engine's sign.</para></summary>
    internal static void SetViewVolume(DbId cam, in AlloyView v)
    {
        if (!SceneDb.Cameras.Valid(cam) || !v.Valid) return;
        Matrix4x4 m = v.View * v.Proj;
        float* p = stackalloc float[24];
        CullPass.Planes((float*)&m, p, PosOf(cam));
        int b = cam.Index * SceneDb.ViewPlanes;
        // CullPass emits left, right, bottom, top, near, far (its own loop order) and inside-POSITIVE; the
        // ENGINE's list -- which is what GetViewVolume(i) must index, because the game's callers index it the
        // engine's way -- is near, far, then the four sides (CX-109 section 1), and
        // inside-NEGATIVE. So: permute, then negate.
        for (int k = 0; k < SceneDb.ViewPlanes; k++)
        {
            int src = Order[k] * 4;
            SceneDb.CamVolume[b + k] = new Plane(-p[src], -p[src + 1], -p[src + 2], -p[src + 3]);
        }
        SceneDb.CamVolumeSet[cam.Index] = true;
    }

    /// <summary>The engine's view-volume plane order, as indices into <see cref="CullPass.Planes"/>' output.</summary>
    private static readonly int[] Order = { 4, 5, 0, 1, 2, 3 };

    /// <summary>One plane of the camera's view volume (<c>Camera::GetViewVolume(int)</c>). Out of range or a
    /// volume this frame's view never filled answers the zero plane, which is what the engine's own
    /// uninitialised list holds.</summary>
    public static Plane ViewVolume(DbId cam, int index)
    {
        if (!SceneDb.Cameras.Valid(cam) || (uint)index >= SceneDb.ViewPlanes) return default;
        return SceneDb.CamVolume[cam.Index * SceneDb.ViewPlanes + index];
    }

    // ---- the three projections the game asks for -----------------------------------------------------

    /// <summary><c>Camera::MapToScreen(Vector, bool)</c> @0x5a3d60, term for term.
    /// <para>The space is settled (<c>Services/Nameplate/NameplateNative.cs</c>, from the disassembly):
    /// <c>planelineintersection</c> resolves against the plane <c>z = -1</c>, so eye space is -Z-forward and
    /// <c>t = -1/P.z</c>; the tail is <c>vp + half + P' * (h/2) / tan(fov/2)</c> for BOTH axes -- the vertical
    /// half-extent scales x as well, which is what makes the result square-pixel. Unnormalised it is
    /// <b>pixels, bottom-left origin, y up</b>, inside the camera's rect; <paramref name="normalized"/> takes
    /// the engine's own tail, <c>x/w</c> and <c>1 - y/h</c>, i.e. a top-down [0,1] coordinate.</para>
    /// <para>A view angle of 0 or less (an ortho camera) returns the zero vector, exactly as the engine's
    /// <c>else</c> branch does -- it has no perspective divide to make.</para>
    /// <para>z is the eye-space depth of the point (negative in front of the camera). The engine's own z here
    /// is Ghidra-opaque (a 12-byte by-value return it does not model), and no caller in the game layer reads
    /// it -- <c>UpdatePopUpBubble</c> and the aim assist take x and y. <c>ponytail:</c> if a caller ever turns
    /// out to read z, this is the term to re-measure.</para></summary>
    public static Vector3 MapToScreen(DbId cam, Vector3 world, bool normalized,
                                      float vpX, float vpY, float vpW, float vpH, float guiScale)
    {
        if (!SceneDb.Cameras.Valid(cam)) return default;
        int i = cam.Index;
        float fov = ViewAngleOf(cam);
        if (fov <= 0f) return default;
        Rect(cam, vpX, vpY, vpW, vpH, guiScale, out float rx, out float ry, out float rw, out float rh);
        Vector3 p = Vector3.Transform(world - PosOf(cam), Quaternion.Conjugate(RotOf(cam)));
        if (p.Z == 0f) return default;   // the engine's own denominator test: equality with zero, no sign test
        float t = -1f / p.Z;
        float half = rh * 0.5f;
        float scale = half / MathF.Tan(fov * HalfFovToRad);
        float sx = p.X * t * scale + rw * 0.5f + rx;
        float sy = p.Y * t * scale + half + ry;
        if (normalized) { sx /= rw; sy = 1f - sy / rh; }
        return new Vector3(sx, sy, p.Z);
    }

    /// <summary>The segment a screen pick casts through this camera, in world space --
    /// <c>Camera::HitCheck(int, int, Vector*, CHitInfo*)</c> @0x5a5e10's own two arms. False means the pixel
    /// is outside this camera's rect, which is the engine's own answer of 0.
    /// <para><b>The ortho arm is not an optimisation, it is the only correct one for a GUI panel.</b> A
    /// camera with a view angle of 0 or less is orthographic (<see cref="BuildView"/> draws it that way), and
    /// <see cref="MouseDir"/>'s <c>tan(fov/2)</c> is then ZERO: every pixel returns the camera's forward axis,
    /// so a perspective-arm pick casts the same ray down the middle of the panel whatever the mouse is doing.
    /// That is why the main menu's buttons could not be clicked. The engine's own ortho arm instead maps the
    /// pixel to a position and casts a segment PARALLEL to world -Z through it, ignoring the camera's
    /// orientation entirely:</para>
    /// <list type="bullet">
    /// <item>the pixel goes rect-relative and is rejected outside <c>[0, w) x [0, h)</c>;</item>
    /// <item>both axes map as <c>(c + 0.5) / rectH * cam+0x4c8</c> -- the VERTICAL extent divides x as well,
    /// the same square-pixel convention <see cref="MapToScreen"/> and <see cref="MouseDir"/> use -- plus the
    /// camera's own x / y;</item>
    /// <item><c>cam+0x4c8</c> is the ortho extent <see cref="BuildView"/> derives, <c>rectH * 0.01</c>, so the
    /// two cancel and the mapping is one hundredth of a pixel per world unit;</item>
    /// <item>the segment runs from <c>pos.z - near</c> to <c>pos.z - near - far</c>, so it looks down world
    /// -Z and the pick is the inverse of the projection the panel was drawn with.</item>
    /// </list></summary>
    public static bool PickSegment(DbId cam, int px, int py, float vpX, float vpY, float vpW, float vpH,
                                   float guiScale, out Vector3 from, out Vector3 to)
    {
        from = default; to = default;
        if (!SceneDb.Cameras.Valid(cam)) return false;
        int i = cam.Index;
        Vector3 pos = PosOf(cam);
        float near = SceneDb.CamNear[i], far = SceneDb.CamFar[i];
        if (ViewAngleOf(cam) > 0f)
        {
            Vector3 dir = MouseDir(cam, px, py, vpX, vpY, vpW, vpH);
            if (dir == Vector3.Zero) return false;
            from = pos + dir * near;
            to = pos + dir * far;
            return true;
        }
        Rect(cam, vpX, vpY, vpW, vpH, guiScale, out float rx, out float ry, out float rw, out float rh);
        if (rw <= 0f || rh <= 0f) return false;
        float cx = px - rx, cy = py - ry;
        if (cx < 0f || cy < 0f || cx >= rw || cy >= rh) return false;
        float unit = rh * OrthoUnitsPerPixel;
        float wx = (cx + 0.5f) / rh * unit + pos.X;
        float wy = (cy + 0.5f) / rh * unit + pos.Y;
        from = new Vector3(wx, wy, pos.Z - near);
        to = new Vector3(wx, wy, pos.Z - near - far);
        return true;
    }

    /// <summary><c>Camera::MouseDir(int, int)</c> @0x5a3bc0, term for term: the normalised WORLD-space ray
    /// through a pixel of this camera's rect. The pixel is in the engine's screen space (the same one
    /// <see cref="MapToScreen"/> returns unnormalised), and the camera-space ray is
    /// <c>((px - x - w/2) * t/(h/2), (py - y - h/2) * t/(h/2), -1)</c> before the camera rotation -- note both
    /// axes divide by the VERTICAL half-extent, as in <see cref="MapToScreen"/>.
    /// <para>Unlike <see cref="MapToScreen"/>, the engine does NOT apply the UI-scale division here, so
    /// neither does this.</para></summary>
    public static Vector3 MouseDir(DbId cam, int px, int py, float vpX, float vpY, float vpW, float vpH)
    {
        if (!SceneDb.Cameras.Valid(cam)) return default;
        int i = cam.Index;
        float rx, ry, rw, rh;
        float cw = SceneDb.CamViewW[i], ch = SceneDb.CamViewH[i];
        if (cw == 0f && ch == 0f) { rx = vpX; ry = vpY; rw = vpW; rh = vpH; }
        else { rx = SceneDb.CamViewX[i]; ry = SceneDb.CamViewY[i]; rw = cw; rh = ch; }
        if (rh <= 0f) return default;
        float t = MathF.Tan(ViewAngleOf(cam) * HalfFovToRad);
        float half = rh * 0.5f;
        var d = new Vector3((px - rx - rw * 0.5f) * t / half, (py - ry - half) * t / half, -1f);
        // RotOf, not the camera's own column: the engine rotates by `this + 0xbc`, which is Gob+0xb4 on the
        // camera's CAurObject half, and the game turns the world camera through that gob rather than through
        // Camera::SetOrientation -- so the column stays identity all session. An unrotated ray cast straight
        // down world -Z from the eye, which is why click-to-move walked nowhere near the cursor and a hover
        // never found a creature to highlight.
        d = Vector3.Transform(d, RotOf(cam));
        float len = d.Length();
        return len > 0f ? d / len : d;
    }

    /// <summary>The camera's forward axis in world space -- the rotation applied to (0, 0, -1). The one term
    /// every caller that wants "is this in front of me" needs, and the discriminator
    /// <see cref="MapToScreen"/> deliberately does NOT apply (the engine's own projection mirrors a point
    /// behind the camera onto the screen, and CX reproduces that so a plate does not move).</summary>
    public static Vector3 Forward(DbId cam)
        => SceneDb.Cameras.Valid(cam)
         ? Vector3.Transform(new Vector3(0f, 0f, -1f), RotOf(cam))
         : default;

    /// <summary>Yaw, pitch and distance come from the camera's BEHAVIOUR, not from the camera
    /// (<c>Camera::GetYaw</c>/<c>GetPitch</c>/<c>GetDist</c> @0x5a3970/@0x5a39c0/@0x5a3a10 each test
    /// <c>behavior-&gt;AsBehaviorCameraFollow()</c> and then read <c>behavior+0x54</c>/<c>+0x5c</c>/<c>+0x48</c>).
    /// The behaviour is a GAME-constructed engine-layout object (ADR 0028 section 2), so CX reads those three
    /// fields off it directly -- which is also how <c>CNWCModule::InterpolateYaw/Pitch/Dist</c> writes them.
    /// Zero when the camera has no behaviour.</summary>
    public static float BehaviorFloat(DbId cam, int offset)
    {
        if (!SceneDb.Cameras.Valid(cam)) return 0f;
        nint b = SceneDb.CamBehavior[cam.Index];
        if (b == 0) return 0f;
        // The TYPE TEST, which is not optional: the engine's own GetYaw/GetPitch/GetDist each call
        // behavior->AsBehaviorCameraFollow() first and return 0 when it is null. A camera can carry a
        // CAurBehaviorSubwindow instead, and that object is operator_new(0x48) -- reading +0x54/+0x5c off it
        // is a heap over-read of someone else's allocation.
        var beh = new CAurBehavior(b);
        if (beh.AsBehaviorCameraFollowFn == IntPtr.Zero) return 0f;
        CAurBehaviorCameraFollow follow = beh.AsBehaviorCameraFollow();
        return follow.IsNull ? 0f : *(float*)(follow.Ptr + offset);
    }

    /// <summary>The three behaviour fields <c>GetYaw</c>/<c>GetPitch</c>/<c>GetDist</c> read.
    /// <c>EngineLayout</c>'s own <c>CAurBehaviorCameraFollow</c> rows, per OS: the object is the GAME's, so
    /// the offsets are the engine's class layout and are build-locked like any other engine read.</summary>
    public static int BehYaw => EngineLayout.FollowYaw;
    public static int BehPitch => EngineLayout.FollowPitch;
    public static int BehDist => EngineLayout.FollowDist;

    /// <inheritdoc cref="DbScene.TakeCounters"/>
    internal static (long, long, long, long) TakeCounters() => (_created, _destroyed, _refused, _views);

    /// <inheritdoc cref="DbScene.TakeCounters"/>
    internal static void PutCounters((long, long, long, long) c)
        => (_created, _destroyed, _refused, _views) = c;

    internal static string Stats(int _)
        => $"cameras: {SceneDb.Cameras.Live} live ({_created} created, {_destroyed} destroyed), {_views} views built"
         + (_refused > 0 ? $", {_refused} REFUSED (table full)" : "");
}
