using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>One view's render state as ALLOY computes it, from the camera object's own fields: the viewport
/// the view covers, the aspect the projection is built at, the projection and view matrices, and the depth
/// range the view writes into -- everything <c>Camera::RenderScene</c> @0x4a7920 derives before it calls
/// <c>Scene::Render</c> (<c>CX-109</c> §1).</summary>
internal struct AlloyView
{
    /// <summary>Row-vector (<c>v * M</c>), the convention the rest of Alloy uses, and byte-identical to what
    /// the engine's own matrix stacks hold.</summary>
    internal Matrix4x4 View, Proj;
    /// <summary>The viewport in engine pixels: the camera's own rect when it has one, else the whole screen.</summary>
    internal int X, Y, W, H;
    internal float Aspect, Near, Far, Fovy, DepthNear, DepthFar;
    /// <summary>False = the view angle is 0 or less, i.e. the ORTHO branch.</summary>
    internal bool Perspective;
    /// <summary>This view has been measured at least once.</summary>
    internal bool Valid;
}

/// <summary>
/// CX-109: Alloy's own camera-to-view/projection step. The per-view loop's arithmetic is Alloy's -- viewport,
/// render scale, aspect, projection, view matrix and depth range all come out of the camera object's fields
/// here, and nothing in Alloy reads <c>m_aurMtxStack</c> or the <c>CurrentCamera</c> latch for them any more.
/// <see cref="World"/> is Alloy's own answer to "which camera is the world camera", latched by this class's
/// own hook and valid for the whole frame.
/// </summary>
/// <remarks>
/// <para><b>Why the engine's loop still runs.</b> The hook measures and then calls the original. The
/// original's remaining consumers are all scene-side and belong to other tickets: the frustum planes at
/// <c>Camera+0x4e8</c> that <c>ManageSceneBSP</c> @0x47fa80 culls with (CX-104), the MODEL matrix stack that
/// <c>Scene::AddPartsTreeToDrawBuckets</c> @0x460880 composes every part's world matrix
/// (<c>VertexPrimitive+0x18</c>) on (CX-103), the <c>CurrentCamera</c> global engine code inside
/// <c>Scene::Render</c> reads, and the GUI views' viewport/upscale/depth-range state whose only consumer is
/// the engine's own GUI traversal (tier 4). It also has to run because <c>Scene::Render</c> -- and so
/// <see cref="RspShim"/> -- is reached through it. When CX-103 and CX-104 land, the world branch can stop
/// calling it; <c>view.cameraloop 0</c> is the A/B that proves Alloy's numbers first, and
/// <see cref="Describe"/> prints the two matrices' largest element difference against the engine's.</para>
/// <para><b>The world view is the simple path, provably.</b> <c>Camera::UpscaleViewport</c> @0x5a44b0 returns
/// false unless <c>|CAurora::GetGUIScale() - 1| &gt;= 0.09</c> AND the scene is one of ~20 named
/// <c>sceneGUI_*</c> panels AND the camera has a viewport of its own -- so for the world camera (viewport
/// 0x0) it is false on every client at every GUI scale. No GUI matrix, no <c>SetViewPortLight</c>, no per-view
/// depth clear (the <c>Clear(2)</c> is GUI-only), depth range [0, 1], and the aspect is the screen's.</para>
/// <para><b>Render scale vs GUI scale.</b> Alloy's own render scale (#391) is deliberately NOT here: it sizes
/// the target the owned passes draw into, not the projection, and since it scales width and height by the
/// same factor the aspect -- and so the projection -- is unchanged. GUI scale reaches a panel view through
/// that camera's own viewport fields, which the engine's loop writes before this measures them, so a measured
/// view carries it.</para>
/// </remarks>
internal static unsafe class AlloyCamera
{
    /// <summary>Degrees to radians, halved: <c>aurMatrixStack::Perspective</c> @0x4db1d0 computes
    /// <c>tanf(fovDeg * 0.017453292f * 0.5f)</c>.</summary>
    private const float HalfFovToRad = MathF.PI / 360f;
    /// <summary><c>Camera::RenderScene</c>'s ortho extent: <c>cam+0x4c8 = viewportHeight * 0.01</c>, and the
    /// ortho box is then <c>(0, aspect*h, 0, h)</c>.</summary>
    private const float OrthoUnitsPerPixel = 0.01f;
    /// <summary>The depth slices <c>Camera::RenderScene</c> @0x4a82e9 hands a view: five named overlay scenes
    /// in front, every other <c>sceneGUI</c> view behind them, a non-GUI view the whole range.</summary>
    private const float GuiFrontNear = 0f, GuiFrontFar = 0.02f, GuiBackNear = 0.021f, GuiBackFar = 0.1f;
    /// <summary>Frames a world-camera sighting stays valid. One, so the answer survives a reader that runs
    /// after the view has closed (which the engine's latch did not) and still goes null within two frames of
    /// the last world view -- a freed camera cannot be sighted again.</summary>
    private const int WorldGrace = 1;

    /// <summary>The <c>m_aurMtxStack</c> entries by <c>Aurora::AuroraTransStates</c>: PROJECTION 0, VIEW 1,
    /// MODEL 2, TEXTURE 3. An INDEX into the global array of stacks, which the view's indexer steps by.</summary>
    internal const int ModeProjection = 0, ModeView = 1, ModeModel = 2, ModeTexture = 3;

    private static ExtenderLog? _log;
    private static CameraRenderSceneFn _orig;
    private static int* _screenW, _screenH, _glViewport;
    private static byte* _stacks;
    private static CameraUpscaleViewportFn _upscale;
    private static GetGuiScaleFn _guiScale;
    private static long _upscaled, _upscaleRefused;
    private static int _frame, _worldFrame = int.MinValue;
    private static IntPtr _world, _active;
    private static AlloyView _worldView, _activeView;
    private static long _worldViews, _guiViews;

    /// <summary>Alloy owns the view/projection step. Off hands it back: the matrices come off
    /// <c>m_aurMtxStack</c> and the world camera off <c>CurrentCamera</c>, which is the A/B for anything that
    /// looks wrong in Alloy's arithmetic.</summary>
    internal static bool Owned => ViewKnobs.CameraLoop.V && _orig != null;

    /// <summary>The world view's <c>Camera*</c>, or zero when there is none -- Alloy's own latch, set at the
    /// world view's <c>Camera::RenderScene</c> and good for this frame and the next.
    /// <para>With <c>view.cameraloop 0</c> this falls back to the engine's <c>CurrentCamera</c> global, which
    /// is null outside a scene render: that is one of the two differences the A/B changes.</para></summary>
    internal static IntPtr World
        => !Owned ? EngineLatch : _frame - _worldFrame <= WorldGrace ? _world : IntPtr.Zero;

    /// <summary>The camera whose view is rendering right now (world or GUI panel), or zero outside a view.</summary>
    internal static IntPtr Active => _active;

    /// <summary>The world view's measured state -- what the Alloy frame takes its view and projection from.</summary>
    internal static ref readonly AlloyView WorldView => ref _worldView;

    /// <summary>CX-120: measure a view from the scene DATABASE's own camera record instead of from the
    /// engine's <c>Camera::RenderScene</c>, which does not run in DB mode.
    /// <para>This class's whole job is "turn a camera object into a view", and seam-scene has already done
    /// exactly that arithmetic to publish the frame -- so the DB path HANDS the answer over rather than
    /// re-deriving it from a camera record the renderer would have to read a second time. Re-deriving is how
    /// the pass and the snapshot end up describing two different cameras one frame apart.</para>
    /// <para>Without this the world view is never measured, <c>DrawOwned</c> refuses with "no measured world
    /// camera" and -- because that refusal is an <c>AlloyGate.Fail</c> -- the session is over: the first DB
    /// area rendered an empty world and said so on screen.</para></summary>
    internal static void NoteDbView(in AlloyView v, bool world)
    {
        _activeView = v;
        if (world) { _worldViews++; _worldView = v; _worldFrame = _frame; }
        else _guiViews++;
    }

    /// <summary>The state of the view rendering right now: what <see cref="GuiCapture"/> records a GUI draw's
    /// view and projection from.</summary>
    internal static ref readonly AlloyView ActiveView => ref _activeView;

    /// <summary>The MODEL matrix every world-frame draw sits on: the IDENTITY.
    /// <para>Derived, not assumed (<c>CX-109</c> §2). Every engine walk brackets its own
    /// model writes with <c>Push</c>/<c>PopCurrentMatrix</c>; <c>Camera::ModifyGUIMatrix</c> @0x5a61e0 -- the
    /// only unbalanced-looking writer -- cannot fire for the world view and is popped anyway; and
    /// <c>AurRenderStrings</c> @0x436480 calls <c>LoadMatrixIdentity</c> with no <c>SetMatrixMode</c> once per
    /// frame, in the mode the frame leaves selected, which is 2. Alloy's own world rendering is the second
    /// witness: it mixes <c>VertexPrimitive+0x18</c> with world-space positions read from engine fields, and
    /// those two only agree in one space.</para>
    /// <para><see cref="Beams"/>, <see cref="SkyBox"/> and <see cref="Particles"/> each read the stack top
    /// once a frame for exactly this value before CX-109.</para></summary>
    internal static Matrix4x4 ModelBase => Matrix4x4.Identity;

    /// <summary>The engine's own latch, for the <c>view.cameraloop 0</c> A/B ONLY.</summary>
    private static IntPtr EngineLatch => _latch == null ? IntPtr.Zero : *_latch;
    /// <summary><c>&amp;CurrentCamera</c>, resolved once at <see cref="Attach"/>: read only on the
    /// <c>view.cameraloop 0</c> A/B, which is still a frame path.</summary>
    private static IntPtr* _latch;

    internal static void Attach(ExtenderLog log)
    {
        _log = log;
        ViewKnobs.Touch();
        _screenW = (int*)Native.Sym(SymbolMap.ScreenWidth);
        _screenH = (int*)Native.Sym(SymbolMap.ScreenHeight);
        _glViewport = (int*)Native.Sym(SymbolMap.GlViewport);
        _stacks = (byte*)Native.Sym(SymbolMap.MAurMtxStack);
        _latch = (IntPtr*)Native.Sym(SymbolMap.CurrentCamera);
        _upscale = EngineFn.CameraUpscaleViewport;
        _guiScale = EngineFn.GetGuiScale;
        if (_upscale == null || _guiScale == null || _glViewport == null)
            log.Always("AlloyCamera: Camera::UpscaleViewport, CAurora::GetGUIScale or GLRender::m_viewport "
                       + "unresolved -- a GUI panel view cannot be measured at a game UI scale other than 1, so "
                       + "those views keep the ENGINE's view and projection (the world view is unaffected)");
        if (_glViewport == null)
            log.Always("AlloyCamera: GLRender::m_viewport unresolved -- the world view's viewport and aspect "
                       + "cannot be computed, so every world frame will stand down (view.cameraloop=0 renders "
                       + "off the engine's matrix stacks instead)");
        if (_screenW == null || _screenH == null)
            log.Always("AlloyCamera: g_nScreenWidth/Height unresolved -- a viewport that spills past the screen "
                       + "cannot be clamped the way SetViewPortLight clamps it; a view that fits is unaffected");
    }

    internal static void Install(ExtenderLog log, HookService hooks)
    {
        _log = log;
        if (!EngineLayout.Require(log, "AlloyCamera", "Camera", "Scene")) return;
        _orig = hooks.InstallCameraRenderScene(&OnRenderScene);
        if (_orig == null)
        {
            // CX-45: not a quiet fallback. Without the hook Alloy has no camera of its own, and the one thing
            // it could do instead -- read the engine's latch -- is what this ticket removed. Say it, and leave
            // the knob as the way back.
            log.Always("AlloyCamera: Camera::RenderScene did not hook -- Alloy has no camera of its own and "
                       + "every world frame will stand down; set view.cameraloop=0 in cx.cfg to render off the "
                       + "engine's matrix stacks instead.");
            return;
        }
        log.Info("AlloyCamera: armed -- Alloy computes each view's viewport, projection, view matrix and depth "
                 + "range from the camera's own fields (view.cameraloop 0 hands the step back to the engine)");
    }

    /// <summary>One view. Cold: once per view per frame, a couple of dozen times at most; allocates nothing.
    /// <para>The original ALWAYS runs -- see the class remarks for the four scene-side outputs that are still
    /// the engine's, and because <c>Scene::Render</c> (and so the shim) is reached through it. The active-view
    /// pair is saved and restored rather than cleared, so a nested view could not blank its parent's.</para></summary>
    [UnmanagedCallersOnly]
    private static void OnRenderScene(IntPtr cam)
    {
        IntPtr savedActive = _active;
        AlloyView savedView = _activeView;
        Measure(cam);
        _orig(cam);
        _active = savedActive;
        _activeView = savedView;
    }

    /// <summary>Compute this view's state and latch it.</summary>
    private static void Measure(IntPtr cam)
    {
        _active = cam;
        _activeView = default;
        if (cam == IntPtr.Zero) return;
        IntPtr scene = new Camera(cam).Scene.Ptr;
        if (scene == IntPtr.Zero) return;   // Camera::RenderScene's own first test: no scene, no view
        // world-scene-discriminator: Scene+0x188 (the static tile BSP root) is non-null iff this is the
        // world/area scene, and Scene::Render forks on the same field.
        var sc = new CX.Engine.Scene(scene);
        bool world = sc.BspRoot != IntPtr.Zero;
        Build(cam, (byte*)(scene + EngineLayout.SceneName), out _activeView);
        if (world)
        {
            _worldViews++;
            _worldView = _activeView;
            _world = cam;
            _worldFrame = _frame;
        }
        else _guiViews++;
    }

    /// <summary>Everything <c>Camera::RenderScene</c> derives before <c>Scene::Render</c>, for one camera:
    /// pure arithmetic over the camera's fields and the engine's own viewport, no GL and no engine call except
    /// the UI-scale query.
    /// <para>The rect is carried in FLOATS through the arithmetic and cast to int only for the viewport, which
    /// is what the engine does: <c>SetViewPortLight</c> takes four <c>(int)</c> casts, while the aspect and the
    /// ortho extent (<c>cam+0x4c8 = h * 0.01</c>) are computed from the raw floats.</para></summary>
    private static void Build(IntPtr cam, byte* sceneName, out AlloyView v)
    {
        v = default;
        var c = new Camera(cam);
        int screenW = _screenW != null ? *_screenW : 0, screenH = _screenH != null ? *_screenH : 0;
        float camW = c.ViewW, camH = c.ViewH;
        float rx, ry, rw, rh;
        if (camW == 0f && camH == 0f)
        {
            // The world view (and any panel camera that never set a viewport) covers the GL viewport, which is
            // where Camera::RenderScene gets it too -- RenderInterface::GetViewPort, i.e. GLRender::m_viewport,
            // rather than g_nScreenWidth/Height. The two agree while ScreenOwner owns the default framebuffer,
            // but the engine's source is the one to read.
            // CX-45: without it there is no aspect and no viewport, and a guess would render the world
            // stretched. The view stays INVALID, which stands the frame down with a reason.
            if (_glViewport == null) return;
            rx = _glViewport[0]; ry = _glViewport[1]; rw = _glViewport[2]; rh = _glViewport[3];
            if (rw <= 0f || rh <= 0f) return;
        }
        else if (Upscale(cam, out rx, out ry, out rw, out rh))
        {
            // A GUI panel view whose rect the engine rewrote for the game's UI scale. Its own fields still hold
            // the unscaled rect at this point -- Camera::RenderScene overwrites them, calls SetViewPortLight
            // and puts them back -- so the rewritten rect is what the aspect and the viewport scales measure.
        }
        else if (_upscaleNotMeasurable)
        {
            // CX-45: the UI scale is not 1 and the query that would tell us the rewritten rect did not resolve.
            // Measuring this view from the unscaled rect would stretch whatever the panel draws, so the view
            // stays INVALID and GuiCapture keeps the engine's own matrices for it.
            _upscaleRefused++;
            return;
        }
        else { rx = c.ViewX; ry = c.ViewY; rw = camW; rh = camH; }
        v.X = (int)rx; v.Y = (int)ry; v.W = (int)rw; v.H = (int)rh;
        v.Aspect = rh != 0f ? rw / rh : 1f;
        v.Near = c.Near;
        v.Far = c.Far;
        v.Fovy = c.ViewAngle;
        v.Perspective = v.Fovy > 0f;
        float unit = rh * OrthoUnitsPerPixel;
        v.Proj = v.Perspective ? Perspective(v.Fovy, v.Aspect, v.Near, v.Far)
                               : Ortho(0f, v.Aspect * unit, 0f, unit, v.Near, v.Far);
        ViewportScales(v.X, v.Y, v.W, v.H, screenW, screenH, out float xs, out float ys);
        v.View = ViewOf(c.Pos, c.Rot, xs, ys);
        DepthRange(sceneName, v.Fovy, out v.DepthNear, out v.DepthFar);
        v.Valid = true;
    }

    /// <summary>Was this view's viewport rewritten for the game's UI scale? <c>Camera::UpscaleViewport</c>'s own
    /// answer, asked rather than ported: the table behind it is ~20 hard-coded <c>sceneGUI_*</c> panel names
    /// and a set of per-panel centring modes, which is the engine's GUI LAYOUT policy rather than render
    /// arithmetic. Only ever asked for a camera that HAS a rect of its own -- the write-out block is gated on
    /// exactly that, so the world view can never take this path.</summary>
    private static bool Upscale(IntPtr cam, out float x, out float y, out float w, out float h)
    {
        x = y = w = h = 0f;
        _upscaleNotMeasurable = false;
        if (_upscale == null || _guiScale == null || _glViewport == null) { _upscaleNotMeasurable = true; return false; }
        // The gate the engine opens with. Asked here too, so the common case costs one float compare instead of
        // 6KB of string compares -- and so "not measurable" means "the scale is not 1 AND the query is gone".
        if (MathF.Abs(_guiScale() - 1f) < UpscaleEpsilon) return false;
        float* vp = stackalloc float[4];
        for (int i = 0; i < 4; i++) vp[i] = _glViewport[i];
        float* into = stackalloc float[UpscaleFloats];
        for (int i = 0; i < UpscaleFloats; i++) into[i] = 0f;
        if (new Camera(cam).CameraUpscaleViewport(vp, (float*)(cam + EngineLayout.CamViewX), into) == 0) return false;
        x = into[0]; y = into[1]; w = into[2]; h = into[3];
        _upscaled++;
        return true;
    }

    /// <summary><c>Camera::UpscaleViewport</c>'s own first test: a UI scale this close to 1 is no scale at all.</summary>
    private const float UpscaleEpsilon = 0.09f;
    /// <summary>Floats <c>Camera::UpscaleViewport</c> writes: rect 0..3, mode 4, offsets 5/6, centring 7/8,
    /// scale 9. Sized above it -- the buffer is a stack frame, and a miscount here is a stack overwrite.</summary>
    private const int UpscaleFloats = 16;
    private static bool _upscaleNotMeasurable;

    // ---- the ported arithmetic -------------------------------------------------------------------------

    /// <summary><c>aurMatrixStack::Perspective</c> @0x4db1d0, term for term. This is <c>glFrustum</c>'s
    /// <c>[-1, 1]</c> depth mapping; <c>Matrix4x4.CreatePerspectiveFieldOfView</c> builds the D3D
    /// <c>[0, 1]</c> one and is the wrong matrix here, which is why this is written out.</summary>
    internal static Matrix4x4 Perspective(float fovyDegrees, float aspect, float near, float far)
    {
        var m = new Matrix4x4();
        float t = MathF.Tan(fovyDegrees * HalfFovToRad);
        if (t <= 0f || aspect <= 0f || far == near) return m;
        m.M11 = 1f / (aspect * t);
        m.M22 = 1f / t;
        m.M33 = -(far + near) / (far - near);
        m.M34 = -1f;
        m.M43 = -(2f * far * near) / (far - near);
        return m;
    }

    /// <summary><c>aurMatrixStack::Orthographic</c> @0x4db320, i.e. plain <c>glOrtho</c>.</summary>
    internal static Matrix4x4 Ortho(float l, float r, float b, float t, float near, float far)
    {
        var m = new Matrix4x4();
        if (r == l || t == b || far == near) return m;
        m.M11 = 2f / (r - l);
        m.M22 = 2f / (t - b);
        m.M33 = -2f / (far - near);
        m.M41 = -(r + l) / (r - l);
        m.M42 = -(t + b) / (t - b);
        m.M43 = -(far + near) / (far - near);
        m.M44 = 1f;
        return m;
    }

    /// <summary><c>GLRender::SetViewTransform</c> @0x4c90c0: identity, the two viewport scales, the camera
    /// rotation's CONJUGATE (<c>operator-</c> @0x5b5d50 negates only <c>w</c>, which is the same rotation as
    /// the conjugate), then a translate by <c>-pos</c>. The engine applies each as a local transform, which on
    /// this side -- row-vector, over the engine's own byte layout -- is a left-multiply.</summary>
    internal static Matrix4x4 ViewOf(Vector3 pos, Quaternion rot, float xScale, float yScale)
        => Matrix4x4.CreateTranslation(-pos)
         * Matrix4x4.CreateFromQuaternion(Quaternion.Conjugate(rot))
         * Matrix4x4.CreateScale(xScale, yScale, 1f);

    /// <summary><c>GLRender::SetViewPortLight</c> @0x4cb4b0's two scales, computed from the rect the view is
    /// about to use rather than read out of <c>m_fViewportXScale/YScale</c>: a viewport that spills past the
    /// screen is clamped and the scale compensates, and reading the globals would let a stale value from a
    /// preceding GUI view leak into the world view, which never writes them.</summary>
    internal static void ViewportScales(int x, int y, int w, int h, int screenW, int screenH,
                                        out float xScale, out float yScale)
    {
        xScale = 1f; yScale = 1f;
        if (h + y > screenH && screenH - y > 0) yScale = (float)h / (screenH - y);
        if (w + x > screenW && screenW - x > 0) xScale = (float)w / (screenW - x);
    }

    /// <summary><c>Camera::RenderScene</c>'s depth decision: a <c>sceneGUI</c>-prefixed scene -- or ANY camera
    /// whose view angle is exactly 0 -- shares one depth buffer split by range, with five named overlays in
    /// front; everything else owns [0, 1]. <see cref="GuiCapture.DepthOf"/> makes the same call from the scene
    /// alone, which is why it could not see the view-angle term; here the camera is in hand.</summary>
    internal static void DepthRange(byte* sceneName, float fovy, out float near, out float far)
    {
        bool gui = fovy == 0f || (sceneName != null && Prefix(sceneName, "sceneGUI"u8));
        if (!gui) { near = 0f; far = 1f; return; }
        bool front = sceneName != null
                  && (Prefix(sceneName, "sceneGUI_MouseOverlay"u8) || Prefix(sceneName, "sceneGUI_Overlay"u8)
                   || Prefix(sceneName, "sceneGUI_pnl_vidopts"u8) || Prefix(sceneName, "sceneGUI_PNL_CONNECTSTAT"u8)
                   || Prefix(sceneName, "sceneGUI_PNL_UTIL_OK"u8));
        near = front ? GuiFrontNear : GuiBackNear;
        far = front ? GuiFrontFar : GuiBackFar;
    }

    private static bool Prefix(byte* s, ReadOnlySpan<byte> want)
    {
        for (int i = 0; i < want.Length; i++) if (s[i] != want[i]) return false;
        return true;
    }

    // ---- frame edge and instruments --------------------------------------------------------------------

    /// <summary>One present: advances the frame a world sighting is stamped against.</summary>
    internal static void NewFrame() => _frame++;

    /// <summary>The engine's own view and projection for the view rendering right now, off
    /// <c>m_aurMtxStack</c>. The <c>view.cameraloop 0</c> path and the parity note's other side -- and, with
    /// <see cref="GuiCapture"/>'s per-draw model and texture matrices, the only reads of that global left.</summary>
    internal static bool EngineMatrices(out Matrix4x4 view, out Matrix4x4 proj)
    {
        view = Matrix4x4.Identity; proj = Matrix4x4.Identity;
        if (_stacks == null) return false;
        var st = new aurMatrixStack((IntPtr)_stacks);
        view = st[ModeView].Top;
        proj = st[ModeProjection].Top;
        return true;
    }

    /// <summary>THE instrument this ticket ships: how far Alloy's own view and projection are from the
    /// engine's, measured at the frame anchor -- which is the one place both are meant to describe the same
    /// camera. Called once per Alloy frame; two 64-byte reads and 32 compares, and it is the evidence a live
    /// session needs before the engine's loop can be dropped (see the class remarks).
    /// <para>Nonzero is not automatically wrong: with TAA on the projection the pass uses carries the jitter,
    /// and this is measured BEFORE it. Zero is the claim.</para></summary>
    internal static void NoteParity()
    {
        if (!Owned || !_worldView.Valid || !EngineMatrices(out Matrix4x4 ev, out Matrix4x4 ep)) return;
        _dView = MaxAbs(_worldView.View, ev);
        _dProj = MaxAbs(_worldView.Proj, ep);
        if (_dView > _dViewMax) _dViewMax = _dView;
        if (_dProj > _dProjMax) _dProjMax = _dProj;
    }

    private static float _dView, _dProj, _dViewMax, _dProjMax;

    /// <summary>What <c>/knobs</c>, <c>/view</c> and the panel row say. It carries the parity numbers, because
    /// "Alloy's matrices equal the engine's" is the one claim this ticket cannot settle off-device.</summary>
    internal static string Describe()
    {
        if (_orig == null) return "STOOD DOWN (Camera::RenderScene did not hook -- Alloy has no camera)";
        if (!ViewKnobs.CameraLoop.V) return "off -- view/projection off m_aurMtxStack, world camera off CurrentCamera";
        string s = $"on, {_worldViews} world / {_guiViews} GUI views";
        if (!_worldView.Valid) return s + ", no world view measured yet";
        if (_upscaled > 0) s += $", {_upscaled} UI-scaled GUI views";
        if (_upscaleRefused > 0) s += $", {_upscaleRefused} GUI views NOT MEASURED (no UpscaleViewport)";
        return s + $", {_worldView.W}x{_worldView.H} {(_worldView.Perspective ? $"fov {_worldView.Fovy:F1}" : "ortho")}"
                 + $" near/far {_worldView.Near:F2}/{_worldView.Far:F1}"
                 + $", vs engine dView {_dView:G3} (peak {_dViewMax:G3}) dProj {_dProj:G3} (peak {_dProjMax:G3})";
    }

    /// <summary>One clause of the period report: the parity pair, which is what a live session reads.</summary>
    internal static string Stats(int frames)
        => !Owned || !_worldView.Valid ? "" : $"cam dV {_dView:G2} dP {_dProj:G2}";

    /// <summary>Largest absolute element difference between two matrices -- the parity number. Not hot: only
    /// <see cref="Describe"/> calls it.</summary>
    private static float MaxAbs(in Matrix4x4 a, in Matrix4x4 b)
    {
        float m = 0f;
        fixed (Matrix4x4* pa = &a)
        fixed (Matrix4x4* pb = &b)
        {
            float* fa = (float*)pa, fb = (float*)pb;
            for (int i = 0; i < 16; i++) { float d = MathF.Abs(fa[i] - fb[i]); if (d > m) m = d; }
        }
        return m;
    }

    /// <summary>Pure-math assertions over every number this class derives, pinned to the decompilation.
    /// Headless-safe: no engine memory, no GL.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "AlloyCamera.SelfCheck");

        // aurMatrixStack::Perspective(90, 1, 1, 100): t = tan(45) = 1.
        Matrix4x4 p = Perspective(90f, 1f, 1f, 100f);
        check.Expect(Near(p.M11, 1f) && Near(p.M22, 1f), "perspective 90/1 has unit x and y terms");
        check.Expect(Near(p.M33, -101f / 99f), "perspective z scale is -(f+n)/(f-n)");
        check.Expect(p.M34 == -1f, "perspective w row is GL's -1");
        check.Expect(Near(p.M43, -200f / 99f), "perspective z offset is -2fn/(f-n)");
        check.Expect(p.M44 == 0f, "a GL frustum has no w translation");
        // ...and the trap this port exists for: .NET's own builder is the D3D [0,1] mapping.
        Matrix4x4 dx = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2f, 1f, 1f, 100f);
        check.Expect(!Near(dx.M33, p.M33), "the engine's depth mapping is NOT CreatePerspectiveFieldOfView's");
        check.Expect(Near(Perspective(90f, 2f, 1f, 100f).M11, 0.5f), "aspect divides the x term");
        check.Expect(Perspective(0f, 1f, 1f, 100f) == new Matrix4x4(), "a zero view angle yields no projection");

        // aurMatrixStack::Orthographic(0, 4, 0, 3, 1, 100).
        Matrix4x4 o = Ortho(0f, 4f, 0f, 3f, 1f, 100f);
        check.Expect(Near(o.M11, 0.5f) && Near(o.M22, 2f / 3f), "ortho scales are 2/(r-l) and 2/(t-b)");
        check.Expect(Near(o.M33, -2f / 99f), "ortho z scale is -2/(f-n)");
        check.Expect(Near(o.M41, -1f) && Near(o.M42, -1f), "a 0-based ortho box centres at -1");
        check.Expect(Near(o.M43, -101f / 99f) && o.M44 == 1f, "ortho z offset is -(f+n)/(f-n)");

        // The view matrix: the eye lands at the view origin, and it comes back out of the inverse -- which is
        // how AlloyRenderer derives uEyePos, so the two must agree.
        var eye = new Vector3(3f, -4f, 5f);
        Quaternion rot = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.7f);
        Matrix4x4 vm = ViewOf(eye, rot, 1f, 1f);
        check.Expect(Vector3.Transform(eye, vm).Length() < 1e-4f, "the eye maps to the view-space origin");
        check.Expect(Matrix4x4.Invert(vm, out Matrix4x4 inv) && (inv.Translation - eye).Length() < 1e-4f,
                     "the inverse view's translation IS the eye");
        // A point one unit along the camera's own forward must land at view z = -1, or every fog and specular
        // term is measured against a different camera than the one the frame draws with.
        Vector3 ahead = Vector3.Transform(eye + FrameRead.ViewForward(rot), vm);
        check.Expect(MathF.Abs(ahead.Z + 1f) < 1e-4f && ahead.X * ahead.X + ahead.Y * ahead.Y < 1e-6f,
                     "the camera's own forward is view -Z");

        // SetViewPortLight's clamp: a rect inside the screen scales by 1, one that spills compensates.
        ViewportScales(0, 0, 800, 600, 800, 600, out float xs, out float ys);
        check.Expect(xs == 1f && ys == 1f, "a viewport inside the screen needs no scale");
        ViewportScales(0, 300, 800, 600, 800, 600, out xs, out ys);
        check.Expect(xs == 1f && Near(ys, 2f), "a viewport spilling off the bottom scales y by h/(screenH-y)");
        ViewportScales(400, 0, 800, 600, 800, 600, out xs, out ys);
        check.Expect(Near(xs, 2f) && ys == 1f, "...and off the right, x by w/(screenW-x)");

        // The depth ranges, by scene name, including the view-angle term GuiCapture cannot see.
        CheckDepth(check, "mainscene", 45f, 0f, 1f, "a world view owns the whole depth range");
        CheckDepth(check, "sceneGUI_PNL_INV", 45f, GuiBackNear, GuiBackFar, "an ordinary panel sits behind");
        CheckDepth(check, "sceneGUI_Overlay", 45f, GuiFrontNear, GuiFrontFar, "the overlay scenes sit in front");
        CheckDepth(check, "sceneGUI_MouseOverlay", 45f, GuiFrontNear, GuiFrontFar, "...and the cursor with them");
        CheckDepth(check, "mainscene", 0f, GuiBackNear, GuiBackFar, "a zero view angle is treated as GUI");
        CheckDepth(check, "sceneGUI_overlay", 45f, GuiBackNear, GuiBackFar, "the prefix compare is CASE SENSITIVE");

        check.Expect(ModelBase.IsIdentity, "the world frame's model base is the identity");
        log.Info(check.Ok ? "AlloyCamera.SelfCheck: passed (projection, view, viewport scales, depth range)"
                          : "AlloyCamera.SelfCheck: FAILED");
    }

    private static void CheckDepth(Checker check, string name, float fovy, float wantNear, float wantFar,
                                   string why)
    {
        Span<byte> buf = stackalloc byte[64];
        int n = System.Text.Encoding.ASCII.GetBytes(name, buf);
        buf[n] = 0;
        fixed (byte* p = buf)
        {
            DepthRange(p, fovy, out float near, out float far);
            check.Expect(near == wantNear && far == wantFar, why);
        }
    }

    private static bool Near(float a, float b) => MathF.Abs(a - b) < 1e-5f;
}
